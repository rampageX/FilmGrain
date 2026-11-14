# FGS 开发约定

- README.md 顶部的两张软件截图之前，只保留项目标题、一句简介和 CHANGELOG 指引。任何新增功能、限制、验证记录或发布说明均放入内容相关章节，禁止插到两张截图之前。
- README 描述当前行为，CHANGELOG 独立记录变化。
- BAT/CMD/VBS 无 BOM；PS1 UTF-8 BOM，禁止中文弯引号。
- 最小改动，GUI/CLI 共用执行核心；未经明确要求不创建 GitHub 分支、PR 或发布。
- WinForms 工具窗口首次显示前应用当前主题并完成布局。含原生子控件的窗口先以透明状态完成首轮绘制，再显示完整界面。预览区先绘制黑底，首帧就绪后切换到 GPU 容器。验收时重启程序，在暗色模式首次打开窗口，检查控件和预览区均无白闪。
- 正式发布以用户确认验收的源码包为唯一基准，核验版本、依赖和构建结果。GitHub Release 长期只保留 Stable.zip 与 GitHub 自动生成的两个源码包。另做 Release_and_Source.zip，可临时上传，用户确认下载后才可删除。独立 Source.zip 和 .sha256 不作为 Release 附件，使用 GitHub 的 SHA256。交付 Release 和整合包链接、更新说明、Commit ID 及验证结果。未经授权不删除旧版、不改变发布结构、不增加附件。遇到冲突先询问。
