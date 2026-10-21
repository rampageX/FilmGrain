# v5.0.20 发布验收与交接

## 本版结果

- 新增 AV1 / HEVC / H.264 NVENC 通用 CQ，与原 VBR 并列。CQ 复用“自动”勾选框，数值直接显示，自动与手动值分别保留，编码器之间和逐文件配置互相隔离。
- 各高级编码区域提供 CQ 最高码率普通文本框；独立开关、默认 80 / 100 / 120 Mbps，2 秒 VBV。低上限可能使画面无法达到目标质量，不能保证总文件体积或设备兼容性。
- 应用工具提示统一深浅主题和长文本换行。保留 FGSIM 实际 shader 像素检查、队列冻结与顺序、预览时间持续状态、LUT 临时参考图、高级分页等已验收行为。

## 已验证

- R5 Windows 返回：`Return_CQ_N7V4PK_R5_U6M9QP_20261021_195835_718.zip`，276 项通过，0 失败，3 项未覆盖。
- RTX 4080、驱动 617.14、FFmpeg 9.0.2：三路 VBR/CQ 短样本真实编码、输出探测、解码和非黑画面；Digital Grain+LUT、FGSIM 真实 shader 像素证据、HDR、主窗口统一/独立队列、取消/失败/新队列通过。
- 默认 CQ 上限进入真实编码命令；自定义 37M/74M 与关闭上限通过生产配置捕获、任务准备和命令断言，没有声称单独测量编码器峰值遵守程度。
- R6 Windows 返回：`Return_CQ_N7V4PK_R6_B4T8NZ_20261021_202112_422.zip`，184 项通过，0 失败；本轮未重复真实编码，复用 R5 证据。
- 普通最高码率文本框对齐与保存重载、CQ 自动/手动切换及数值恢复、逐文件快照、Windows 原生提示框 Popup/Draw 和 dark/light/dark 切换通过。
- 三个编码器的原版/当前 VBR 参数签名一致；中文、空格、单引号、方括号、逗号、分号、&、% 路径通过受测路线。
- 用户已明确确认 R6 人工验收通过。功能代码在发布整理中未再修改；仅变更版本常量。
- 发布候选两份 EXE 由同一源码构建，产品版本 5.0.20、文件/程序集版本 5.0.20.0。正式包仍由 Windows 发布入口重新构建。Microsoft .NET Framework API 检查 2299 项解析、0 失败；候选 EXE SHA256：`43e960734388be5c5908b8c7625ad42db9dcef976a0261694114aaad5c18b95c`。

## 保留边界

- 所配 FFmpeg 不含 `fruc_vulkan`，FRUC 实际插帧未覆盖。
- 隔离沙箱未接入用户私有 OpenSVPFlow venv，SVP 实际插帧未覆盖；不表示用户机器缺失 SVP。
- 未强制触发驱动特定回退/重试；没有将这些情况计作通过。
- 合成 HDR 样本验证元数据与流程，不充当校准后的 HDR 视觉参考。

## 清理

关闭 FGS 和测试进程，备份需要留存的 Return 包，双击根目录 `Clean_CQ_Tests.cmd`。

入口先执行清理器自身的 Windows 夹具/目录联接测试，再执行实际清理；失败即停止并显示错误。只处理 `Tests_CQ_N7V4PK`、`Tests_Cleanup_CQ_N7V4PK`、根目录 `Return_CQ_N7V4PK_*.zip` 和 docs 下本轮三份原始记录。联接/符号链接只删除链接，不遍历目标。

保留用户 INI、图片、共享资源、Lang、Utils、Generated、Logs、LUT、Grain、SVP、私有环境、其他测试目录及返回包。保留本文件和 `CQ_AUDIT_v5.0.20.md`。清理器在本环境未执行 Windows 联接自测；该自测会在双击入口中强制先运行。

## 维护者发布步骤

1. 在实际仓库覆盖本次完整源码或累计补丁，关闭程序，运行 `Clean_CQ_Tests.cmd`。
2. 运行 `release.bat --dry-run 5.0.20`，由现有 Windows 构建/发布检查流程验证版本和打包。此处不是用户 Git 仓库且没有原生 Windows，因此没有声称已执行该 Dry Run。
3. Dry Run 成功后，维护者运行 `release.bat 5.0.20`。正式 ZIP 名称为 `FilmGrain_Studio_v5.0.20_Stable.zip`。

完整源码和补丁不携带本地 `FilmGrain_Config.ini`，覆盖时保留维护者现有配置；默认模板正常更新。

本次交付为 ReleaseReady 源码及累计补丁，未执行 git commit、tag、push 或 GitHub 发布。所有既有用户图片/资源和 `.gitignore` 均保持上传基线字节不变。
