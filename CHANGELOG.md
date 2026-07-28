# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/) 与语义化版本。

> **发布流程**：改根目录 `VERSION` + 本文件新增条目 → commit 推 main（常规 CI 全绿）→
> `git tag vX.Y.Z && git push origin vX.Y.Z` → Release 工作流自动校验
> tag↔VERSION↔CHANGELOG 一致性、跑双端测试、出 macOS `.app` zip 与 Windows x64 zip
> 并挂到 GitHub Release。

## [未发布]

### 修复（Windows）

- **托盘右键菜单中文被截断**——菜单项「显示 / 隐藏」实机渲染成「显示 / 隐」，
  文字直接贴死右边框、无右内边距。根因不在内容测量而在宿主窗尺寸：
  `ContextMenuMode=SecondWindow` 把 MenuFlyout 放进 H.NotifyIcon 自建的
  ~145px 宽窗口，而 XAML flyout 无法超出所在 XamlRoot 的边界（GDI 实测该串
  在菜单字号 14px 下自然宽 78px，项内可用文本区仅 85px 且还要扣快捷键列），
  调 `MinWidth`/`Padding` 均无效。改用 `ContextMenuMode=PopupMenu`（原生
  Win32 `TrackPopupMenu`），按文本自动定宽，CJK 不再截断（菜单 145→161px）。
- **随之修复：托盘菜单点击全无反应**——原生模式下 H.NotifyIcon 只执行菜单项的
  `ICommand`，无法触发 XAML 的 `Click` 路由事件（程序集里只有 ICommand/CanExecute
  通路），四个菜单项原先全绑 `Click=` 故集体失效。改为绑 `Command`；「总在最前」
  取反基准从 `IsChecked` 改为 `App.Settings`（原生菜单只单向读 `IsChecked` 画勾、
  不回写，读它会永远取到旧值），并回写 `IsChecked` 保证下次开菜单勾选态正确。
  实机四项逐一验证：显隐双向、开关双向 + 勾选同步、设置窗打开、退出且无残留图标。

## [0.4.1] - 2026-07-28

> 双端拉平轮：Windows 端补完 Phase C' 的 W0–W6（含一处丢命令缺陷），
> mac 端 caption 回归原生，两端各自把对方发现的泄漏补上。

### 修复（数据缺陷）

- **Windows：排队命令补录（W0）**——一轮跑动中输入、被 mid-turn 消费而不再以
  `type=user` 行重放的 prompt，此前在 Windows 时间线永不出现（mac 早有该路径）。
  实机验证：217 条 `queued_command` 中 200 条是注入块、**净增 17 条真实用户命令**。
- **双端：`!cmd` 直通 shell 泄漏**（Windows W0 实机重扫时肉眼抓到）——`!git pull`
  这类操作会以**两条**节点进时间线（`<bash-input>` 与 `<bash-stdout>` 各一条）。
  按语义分治：输出侧 `<bash-stdout>`/`<bash-stderr>` 不是人说的话 → 加入 L1 忽略
  前缀；输入侧是用户真实操作 → 转为 `$ cmd` 保留（与 slash 命令 convert 同思路）。
  Windows 语料实证 20 条并已清库；mac 侧本机语料 0 命中但**代码同样无处理**，
  属潜伏缺陷，本轮一并补上。

### 修复（Windows 双端拉平 W1–W6）

- **摘要重试与 attempts 上限（W1）**：此前 CLI 偶发超时后节点永停在规则摘要
  （须重启 App），永久失败节点每次启动无上限重跑烧配额；现与 mac 一致——失败
  重入队、上限 3 次、设置「应用」时清零。
- **摘要队列改最新优先（W2）**：回填数百节点时不再让你盯着的顶部最后才拿到
  LLM 标题。
- **结果行时间戳护栏（W3）**：节点乱序入库时不再把旧回复挂到更新的命令上。
- **摘要 prompt 补 agent/project 上下文（W4）**、**provider 请求构造对齐**
  （temperature 0、`/v1` 自动补全、超时 60s）（W5）、**截断改按 grapheme 簇**
  （不再劈开 ZWJ/变体选择符）（W6）。

### 变更

- **macOS caption 改用原生交通灯**：自绘 `×` 换成系统绘制的关闭按钮
  （styleMask 补 `.closable`——此前按钮存在但被禁用），hover 揭示符号、非 key 态
  置灰等全是系统原生行为；⌘W 与按钮走同一条 `windowShouldClose` 路径，语义为
  **收回菜单栏、进程驻留**。窗口 `title` 补齐，Mission Control 与截图选择器可识别。
  **只给关闭**是实测结论：NSPanel 的最小化按钮默认禁用（须显式 `.miniaturizable`），
  而挂件无 Dock 图标、最小化无处可去；缩放对半透明侧栏时间线亦无意义——macOS
  自家工具面板（字体面板、检查器）同样只给关闭。
- **头部与交通灯同排**：SwiftUI 默认为标题栏保留安全区，会把头部整体下压一行；
  现让内容顶到窗口顶并对齐 28pt 标题栏，标题/过滤器/工具按钮与交通灯落在同一行
  （Safari/Finder 工具栏的原生关系），回收一整行竖向空间。

> Windows 端 caption 维持其自身原生约定（右上角三键，任务栏语境下最小化有意义），
> 双端「各自原生」是本产品既定原则，非遗漏。

### 双端一致性

`docs/TEXT-NORMALIZATION.md` §4 现有 **12 条已拉平**（W0–W6 全部标记完成并移入
§4.1）；剩余为 mac 侧 zcode 解析器（Roadmap M4）与两条需先定规范的共同待定项。
测试：mac 34、Windows CoreSmokeTest 253，双端全绿。

## [0.4.0] - 2026-07-28

> 主线：**时间线文本治理**双端收口——把混进时间线的 harness 注入块、markdown 标记
> 清理干净，同时堵住两端各自的丢命令缺陷。

### 修复（数据缺陷，优先看这两条）

- **macOS：slash 命令此前根本不产生节点**——`<command-name>` 回显块被整条丢弃，
  而它是该命令的唯一记录。本机语料实测 79 条 slash 命令 0 产出，修复后 79/79 全部
  复原（两种字段序皆覆盖，非空 `<command-args>` 是用户真实输入，拼回正文）。
  ⚠ 仅对新数据生效：文件偏移已持久化，历史上已被丢弃的命令不会回溯补录。
- **Windows：`<task-notification>` 等注入块以「用户命令」身份泄漏进时间线**（实机语料
  793 次，最大漏源）；L1 七类前缀过滤 + 命令块双字段序 convert 落地，库内 56 条历史
  泄漏节点已清除。

### 新增

- **L2 文本规整层（双端）**：`TextNormalizer` 三档纯函数（Excerpt / Summary / Mining），
  逐行状态机做块级判定（围栏闭合才 skip、表格行首尾锚定、水平线、ATX 标题需尾随
  空格），行内保护后再变换（链接需验 target 形态、强调禁跨行、回填 verbatim）。
  规则表经三方独立审查定稿，双端共读 `docs/normalize-cases.tsv` 48 条 golden 基准
  + 幂等断言。结果行/规则摘要/词典摘录三处作用点，命令原文永不改写。
- **结果行语义对齐**：两端统一为「规整 → 首个非空段落 → ≤500」（mac 原为全文拍平
  截 160），空串双保险兜底。
- **展示完整性分层（§3.5）**：存储只留防御护栏（双端同表 `DisplayLimits`：标题 120 /
  要点 200×6 / 结果行 500），完整内容交给三级渐进披露——折叠态钳制不变、展开态解除
  全部钳制、hover tooltip 兜全文。折叠态观感逐像素不变。
- **来源 agent 徽标（mac 补齐）**：条目元信息行与项目下拉共用双字母色块（CL/CO/KI/ZC），
  项目下拉徽标跟随最近活跃 agent，与 Windows 视觉对等。
- README 补 macOS 实机一览四图（与 Windows 同规格、同演示数据集拍摄）。

### 修复（其他）

- macOS：增量审查确认的 6 处 ICU/.NET 分叉——哨兵回填须 ordinal（否则组合字符旁的
  私用区字符会写进结果行并永久留库）、行尾 TrimEnd 须覆盖全角空格（否则中文语料的
  首段边界整体错位）、正则须开 `useUnixLineSeparators`、assistant 分支补 `isSidechain`
  守卫（子 agent 输出被当成父会话结果行）、回显块判定须先 trim。
- Windows：Kimi 结果通道改走 ContentPart（TurnEnd payload 实测 40/40 为空）、摘要
  prompt 输入按 4000 截断、气泡内容可看全（文本点击展开）、unpackaged 下窗口图标补齐。

### 已知欠账

- Windows 侧 7 项待同步（`docs/TEXT-NORMALIZATION.md` §5.3 W0–W6），其中 **W0
  `attachment.queued_command` 补录缺失**属同类丢命令缺陷（本机语料 4 条实证）；
- macOS 侧 zcode 解析器仍为惰性桩（README Roadmap M4）。

## [0.3.0] - 2026-07-27

### 新增
- **版本体系与 Release 流水线**：仓库根 `VERSION` 为双端唯一版本源（Windows csproj
  构建期注入 assembly 版本、mac `build-app.sh` 注入 Info.plist），推送 `v*` tag 自动产出
  双端 release 包（tag↔VERSION↔CHANGELOG 三方一致性硬门禁 + 双端测试前置）；
  Windows 设置界面显示版本号。
- **来源 agent 徽标**：时间线条目元信息行与项目下拉共用同一视觉的双字母色块徽标
  （CL/CO/KI/ZC + agent 色，`AgentKind.Monogram()` 单一来源）；项目下拉徽标跟随
  **最近活跃**的 agent（多 agent 项目 tooltip 给按最近活跃排序的完整分布）。

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
