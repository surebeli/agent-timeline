# macOS 截图任务书（README 特性展示，即贴即用）

> 复制「开工 prompt」一节给 mac 机器上的 agent 会话即可开工。
> Windows 端同任务已完成（README「Windows 实机一览」即参照物与成品格式）。

## 开工 prompt

你在一台 macOS 机器上，当前目录是仓库 agent-timeline 根目录（远端 github.com/surebeli/agent-timeline，先 git pull 到最新 main）。产品是双端桌面挂件 "Agent Timeline"，mac 端（macos/）已实机验证。

你的任务：为 README 补一组 **macOS 实机特性截图**，与已有的「Windows 实机一览」同规格同内容。

必读（动手前）：
1. README.md 的「Windows 实机一览」小节——成品格式与四张图的参照物（docs/assets/screenshot-windows-*.png）；
2. docs/DEMO-DATASET.md——**截图数据集唯一事实源**（双端内容必须一致）；windows/scripts/demo-seed.py 是 Windows schema 的参考实现；
3. macos/Sources/AgentTimeline/Core/Store.swift——本端 schema（注意与 win 不同：nodes 为 TEXT 主键、ts 为 unix 秒 REAL、无 summary_pending；codenames 的 status_node 为 TEXT）；
4. mac 端设置与 replay 标记存 UserDefaults（AppDelegate/设置相关源码），数据库路径从源码确认（AppPaths 等价物）。

铁律（Windows 端拍摄的血泪教训，逐条遵守）：
1. **隐私红线**：公开截图绝不能出现真实时间线（真实项目名/命令原文）。一律灌注 DEMO-DATASET 演示数据；
2. **数据安全**：拍摄前备份真实 db（及相关 -wal/-shm）与设置；换库用**文件级交换**，不要删目录（win 端曾因子目录被进程占用导致目录级还原失败）；拍完立即还原并用节点计数核验（还原前后 select agent,count(*) 对比）；
3. **隔离干扰**：演示配置=摘要引擎纯规则 + 全部 agent 监听关闭 + 回填 0 天（防真实 session 混入、防烧 LLM）；replay 版本标记设为当前值防启动重放改写手工词典；
4. 演示数据在场时间越短越好；任何一步失败先还原再排障。

拍摄清单（四张，对齐 win 版构图；mac 半透明窗建议在 hover 态≈0.95 不透明度下拍，暗色外观）：
- docs/assets/screenshot-macos-timeline.png —— 主面板全景：今天/昨天分组、≥3 家 agent 徽标、kind 彩标、节点 #11 的 N2✓/N3△ chips、✦ 提炼块、→ 结果行、#8 决策菱形锚点；
- docs/assets/screenshot-macos-projects.png —— 项目过滤器展开态；
- docs/assets/screenshot-macos-dictionary.png —— 代号词典面板（六代号全状态）；
- docs/assets/screenshot-macos-settings.png —— 设置界面。
技术提示：`screencapture -l <windowId>` 抓单窗（windowId 用 Quartz CGWindowListCopyWindowInfo 按 owner 名查）；Retina @2x 输出尺寸大，README 中靠 width 属性控制显示宽度。

可选加分项（建议做，工作量小、双端一致价值大；不做则在 README 措辞中如实区分）：
mac 端尚未实现 win 端两个视觉特性——① 时间线条目元信息行的来源徽标（16px 圆角色块 + 双字母缩写 CL/CO/KI/ZC，见 win 的 AgentKind.Monogram 与 MainWindow.xaml 条目模板）；② 项目过滤器每项的「最近活跃 agent」徽标（分布查询按 MAX(ts) 降序，tooltip 给完整分布）。同步实现后再拍，双端截图即完全对等；实现时给 swift test 补对应断言。

交付：
1. 四张 PNG 入 docs/assets/（命名如上）；
2. README「Windows 实机一览」后新增「macOS 实机一览」同款三列表格 + 设置图链接，并删除 Windows 小节尾部「mac 端截图见页首（更多待补充）」一句；
3. swift test 全绿；真实数据已还原并核验；中文 commit；push 后 CI 四道关全绿；
4. 汇报：完成项 / 拍摄期间新发现的 mac 端问题（若有）/ 遗留项。

范围外（另有安排，本轮不做）：zcode 解析器 mac 同步（SESSION-FORMATS §4，M4）。
