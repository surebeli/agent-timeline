# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/) 与语义化版本。

## [0.2.2] - 2026-07-27

### 新增
- **zcode 通道点亮**（Windows）：解析 `~\.zcode\cli\agents\sess_*\agent_*\transcript.jsonl`
  （turn_started → 任务命令节点，turn_complete → 结果行+代号挖掘，metadata.json cwd → 项目名），
  默认根自动监听、设置可覆盖；实机回填 36 任务节点验证。CoreSmokeTest 90→110 断言。
  待办：SESSION-FORMATS §4 规范补写与 mac 端解析器同步（双端共享层，按约定报请确认）。

### 修复（实机人值守反馈）
- **面板内弹层触发降透明**：chip 详情/词典/右键菜单/过滤菜单打开即夺走激活 → 主窗降到
  0.25，浮层悬在近透明面板上无法阅读——弹层打开期间钉在 hover 不透明度，关闭后按指针
  状态回落。
- 时间线重建合并（回填/批量每节点一次整表重建 O(N²) → 一泵一建）；跳转旧节点的分页循环
  同步收敛。
- 摘要 JSON 提取对 codex stdout 杂讯花括号免疫（平衡候选后向优先）；截断代理对安全；
  设置文件并发写加锁+原子替换；面板尺寸按窗口 DPI 缩放；kind 过滤下 LLM 改判即时
  增删节点。

## [0.2.1] - 2026-07-26

### M3 Windows 实机验证完成

Windows 端从「CI 编译通过」推进到「实机运行验证完毕」（Win11 Enterprise 26200，1706x960 @100%）：
分层验证清单 §2a–§2e 全项完成注记（`windows/DEBUG-PLAYBOOK.md` 留档），
CoreSmokeTest 85→90 断言全绿。

### 修复（实机发现 11 处）
- 种子脚本 UTF-8 无 BOM 在 Windows PowerShell 5.1（GBK 系统）解析崩溃；无参重跑对 watcher 不可见（原地重写不改 fileId/长度）。
- **分页游标与排序键不一致**：id-only 游标在多 agent 回填（ts 更旧但 id 更大）下永久丢行、加载更多空转——改 (ts,id) 复合游标（新增 5 冒烟断言）。
- watcher 内置 session root 不存在时整条通道永久死寂（现挂 watcher 前预创建）；缓冲溢出丢事件无兜底（现 Error → 幂等补扫）；文件偏移在事件入库前持久化（崩溃窗口丢数据，已调序）。
- **CLI 摘要在 Windows 上永远静默降级**：prompt 经 cmd.exe 传给 .cmd shim 时转义必坏（BatBadBut 同类）——改 stdin 传递；超时不杀进程树（cmd 孙进程僵尸烧配额）——Kill(entireProcessTree)；用户侧 SessionEnd hook 拖住进程令结果随超时丢弃——结果信封到手即收针；PATH 带引号目录漏检 shim。
- **粘性日期头跳跃滚动后冻结**（ViewChanged 早于 ItemsRepeater 再实现化）——布局后校准一拍。
- 失焦改写 SystemBackdropConfiguration.IsInputActive 令 Acrylic 塌成实心 fallback（挂件常态即失焦）。
- H.NotifyIcon ForceCreate 默认把进程打入 Win11 EcoQoS 效率模式且永不解除。
- 托盘退出在主窗隐藏时不生效（WinUI #5931）→ 无入口僵尸进程——显式 Close + Environment.Exit 兜底。
- 窗口记忆坐标在显示器变化后越界 → 挂件永久不可见（无 Alt-Tab 可救）——现校验相交回退首启位。
- 340px 头部装不下双 ComboBox 过滤器（溢出压标题）——改紧凑 Button+MenuFlyout，标题列可省略。

### 平台 deviation 终版（详见 windows/README.md 更新记录 (f)）
1. 粘性日期头为 ViewChanged+布局后校准模拟（mac 原生 sticky header），一拍校准延迟不可感知；
2. 窗口渐变用 transitionMs(180ms)，hoverFadeMs(120ms) 归条目内渐显，双端同义；
3. 头部过滤器为紧凑按钮+单选菜单形态，空间不足时标题省略（WinUI 控件 chrome 宽于 mac）；
4. 「总在最前」代码路径正确，验证机会话系统级禁止一切窗口置顶，待正常会话复测；
5. Border 描边内缩 9/7px vs mac 8/6px（故意未补偿，既有已知项）；
6. 非激活面板不抢焦点为 mac NSPanel 特性，Windows 无等价物（既有已知项）。

## [0.2.0] - 2026-07-26

### 新增
- **代号生命周期**：`N1`/`T2` 批量短代号经定义式登记（用户命令与 agent 回复双通道挖掘）+ 词典引导精确匹配；状态机 定义 → 进行中 → 完成 / 变更（含否定语境检测、定义句自提及排除）；定义可重述更新并保留首次定义节点；技术词汇停用表 + LLM 名称合法性门；带版本号的历史重放（崩溃安全）。
- **阶段锚点**：节点按 需求/任务/调研/学习/决策/修复 归类（LLM 主判 + 规则兜底），彩色标签 + 阶段过滤。
- **代号词典面板**：📖 一屏回忆全部代号（状态 + 定义 + 最近提及），点击跳转定义节点。
- **双墨线台账视觉**（设计评审团合成方案）：`❯ + 实线 agent 色墨线 + 高不透明纸面 = 我的原话`（默认常显、失焦仍清晰），`✦ + 虚线灰墨线 + 次级纸面 = 机器提炼`；连续 rail + kind 标记语法（菱形锚点 / 圆点 / 定义环）；今天/昨天粘性日期分隔；整条点击展开、hover 复制、右键菜单。
- **自稳对比**：`panelScrim` 底幕 + `surfaceStroke` 1px 描边，暗色 IDE/terminal 垫底时界面不融底，透明特征保留。
- **CI**：macOS 测试与打包、Windows Core 跨平台冒烟（85 断言）、WinUI 实验性编译、双端 design tokens 同源校验。
- macOS 应用图标。

### 修复
- 增量对抗审查确认的检测语义缺陷 12 项（行内冒号定义漏检、停用表短码缺口、否定误判、重放原子性等）。

## [0.1.0] - 2026-07-25

### 新增
- **M1 macOS MVP**：FSEvents 增量 tail 跟踪 Claude Code / Codex / Kimi session（zcode 预留适配器）；SQLite 存储；`claude -p` headless / OpenAI 兼容 provider / 纯规则 三级摘要引擎；非激活半透明浮窗（hover 透明度、置顶、划选复制、位置记忆）；menu bar 入口；design tokens 构建期嵌入二进制。
- **Windows WinUI 3 全套源码**（Core 层跨平台编译验证），共享 `docs/SESSION-FORMATS.md` 解析规范与 `design/design-tokens.json` 视觉规范。
- 首轮 ultracode 审查修复 11 项（FSEvents 重启 use-after-free、摘要队列管道死锁、Claude 排队命令丢失等）。
