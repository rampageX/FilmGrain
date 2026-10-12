# Film Grain Studio v4.8.15

本版完成应用运行时 .NET 迁移。以下历史测试条目保留当时状态，当前验收结果以本条为准。

- 根 .NET EXE 和串行队列固定 Native，删除原生选择框、Legacy 兼容后端和未知准备失败转 Bridge BAT；统一准备入口保留实际原因，输入错误/依赖不足/不支持组合明确停止。
- 执行器和队列拒绝脚本及 CMD/PowerShell/VBS 宿主；保留 FFmpeg、grav1synth、Python/VapourSynth 等实际算法依赖。
- 旧 CLI、旧 PowerShell GUI、Bridge/不重编码 BAT 和旧助手共 29 个脚本归入 Legacy，共享配置、Lang、SVP vpy/插件、FGSIM 模板/纹理保留原位置；根 EXE 可独立运行。
- 累计接管三线隔行 HDR AUTO/OFF、HDR 保留/转换、设备分离与过滤顺序、已有主输出补上传；隔行 OpenSVPFlow 按既有规则绕过光流，原不支持组合保持明确边界。
- 成功探测未发现 AV1 Film Grain metadata 正确标识 ADDED；未知/失败探测不能当作无颗粒。添加/替换保留输入、现有命名和时间轴行为。
- Windows Full 101 编码＋6 边界通过；Next 29＋10 合并 39 技术项、Native-only 6＋1 合并 7 技术项通过，合并结果不是单次完整矩阵重跑。用户确认相关人工项目及最新 ADDED 画面/同步；未测硬件/容器/时间戳/剪辑及任意组合不扩大为通过。
- 发布工具统一源码暂存、Dry Run 覆盖与正式 ZIP 的目录清单；纳入 Legacy 新增并记录旧路径删除，排除 Tests_Shared、用户配置/缓存/环境、备份和测试交付标记；直接从 .NET 源码核对语言键，不再执行旧 GUI/语言脚本。
- 发布命令在实际构建前同步 .NET ReleaseVersion 和 Stable 标签、README/TXT、基线、CHANGELOG 和 Release Notes；正式构建重新编译，核对根/兼容 EXE 一致及其实际版本常量。下一正式版本号由维护者指定，Dry Run 不等于发布。
- 发布准备 Windows 补测修复：按 Git 已跟踪/未忽略新文件清单过滤并逐字面分批暂存，兼容现有 .gitignore；构建函数显式接收发布版本，保留编译后实际 EXE 版本/Stable 校验。产品源码、算法和已验收 EXE 不变。
- 中文 Git 仓库路径校验直接使用重定向 UTF-8 输出，避免 Windows PowerShell 5.1/CP936 将 Git 根目录读成乱码；共享 Git 文件清单读取，不改变控制台编码，保留顶层目录与非仓库停止边界。
- 发布暂存允许仓库已有 benchmark 历史报告继续跟踪，退出本次报告改动并仅在 Stable 排除；Dry Run 保留相同 HEAD 历史，避免与正式发布校验不一致。失败前已写入目标版本的工作区支持依据 HEAD 基线重试同一版号。
