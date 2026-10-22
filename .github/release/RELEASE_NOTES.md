# Film Grain Studio v5.0.22

- 正式 Stable 改为精简便携运行包，保留程序、默认配置、语言文件和运行资源，不再附带 dotnet 源码、Legacy 历史实现、项目截图及开发发布文件。
- 本地发布构建同时生成 Source 源码包及 SHA256，与 Stable 使用同一次已提交源码归档；源码包记录版本、commit 和对应运行 EXE 的 SHA256。
- 发布运行包清空默认配置中的本机工具、颗粒和 LUT 路径，首次运行按提示指定或安装依赖；开发目录中的默认配置保持原样。
- GitHub Release 仅自动发布 Stable 运行包及其 SHA256；Source 保留在本地输出和工作流 Artifact 中，编码功能和参数保持不变。
