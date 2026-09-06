AV1 Grain Table 目录（Film Grain Studio）

推荐按输出分辨率分类：

  720p\
  1080p\
  1440p\
  2160p\

Film Grain Studio 会递归扫描所有子目录中的 .tbl / .txt，不需要修改第三方表文件名。
GUI 中选择“现成 Grain Table（影视 / Photon）”后，会根据源视频宽度自动匹配 720p / 1080p / 1440p / 2160p 档位，默认只显示对应目录内容；同档位内带实际宽高命名的表按尺寸接近程度优先排序。右侧 ↻ 可重新扫描，↻ 后面的无文字复选框勾选后显示全部分辨率目录，鼠标悬停可查看 ToolTip。

推荐 Grain Table 来源：
  https://github.com/Boulder08/chunknorris
  https://github.com/nekotrix/AV1-Photon-Noise-Tables

Chunk Norris 现有影视表可按文件名中的 720p / 1080p / 1440p 移入对应目录。

4K / 2160p Photon Noise 推荐来源：
  https://github.com/nekotrix/AV1-Photon-Noise-Tables

该仓库提供多种 4K 实际帧尺寸，包括 3840x1600 / 1604 / 1608 / 1616 / 1632 /
2016 / 2064 / 2080 / 2160，以及 2880x2160；3840x2160 同时提供 sRGB 与 BT.2020，
并有 ISO 100 ~ 6400 多档。建议优先选择与最终输出实际宽高完全一致的表。

正式发布包不重新分发第三方 Grain Table 数据，只提供加载、分类与命名解析支持。
