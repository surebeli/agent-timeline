# Agent Timeline — Windows 端（M3 scaffold）

WinUI 3（Windows App SDK）+ C# / .NET 8 实现的桌面半透明时间线挂件。与 mac 端共享
`docs/SESSION-FORMATS.md` 解析规范与 `design/design-tokens.json` 视觉规范。

> ⚠️ **重要：本工程在 macOS 上编写，尚未在 Windows 上编译运行过。**
> 代码按可编译标准编写，但请预期少量琐碎修正（NuGet 版本号、个别 API 签名等）。
> 详见下方「已知未验证事项」。
> 实机调试请从 **[DEBUG-PLAYBOOK.md](DEBUG-PLAYBOOK.md)** 开始（含种子数据脚本与分层验证清单）。
>
> ✅ 已验证部分：`Core/`（解析器/Store/词典/摘要引擎/协调器）与 `Interop/` **不依赖 WinUI**，
> 已在 macOS 用 .NET SDK 实际编译通过（0 警告 0 错误），并对三个解析器的过滤规则、
> 代号正则、SQLite 读写/去重/偏移表、摘要 JSON 契约跑过功能冒烟测试（全部通过）。
> 冒烟测试工程在 `windows/CoreSmokeTest/`（独立 console 工程，未挂进 .sln），
> 在仓库任意平台 `dotnet run` 即可复跑。
> 未验证的主要是 UI 层（XAML / WinUI API / H.NotifyIcon / Win32 interop 行为）。

## 更新记录

- **2026-07-29 引子续接实机验证 + README 一览重拍（v0.5.1 轮，无产品代码改动）**
  - **A 引子续接差分执行**：§3.3b 的实现随 mac 同步落地并过 CI，本轮补 CI 做不了的
    实机验证——本机 15020 条真实 agent 回复（claude 5052 + codex 9968），改动前/后
    两个源码状态各跑一遍 `ParserUtil.ResultExcerpt` 逐条比对：产出变化 3136 条
    （20.9%）、**变短 0 条**、**旧值全部是新值的前缀**、冒号结尾 4496→1369、
    均长 85→127、空串 0→0。两条硬约束成立。冒号残留与 mac（2 条）差一个量级，
    逐条分桶归因：1341 条回复本来就只有一段、19 条正文全在围栏/表格里、9 条吃到
    上限，未归类 0——不是实现少接了。工具入仓 `scripts/leadin-diff/`；
  - **B README「Windows 实机一览」重拍**：三张图统一为同演示数据 / 同 dip 几何 /
    同背板，README 三列统一 290。拍摄脚本入仓 `scripts/shots/`。四处 mac 参数
    按 Windows 实机事实改（弹层受 `ShouldConstrainToRootBounds` 约束不溢出面板、
    合成鼠标输入被系统吞、UIA 树会退化、窗口矩形比客户区大 7px 且 PrintWindow
    不带圆角），全部实测并写进 [DEBUG-PLAYBOOK.md](DEBUG-PLAYBOOK.md) §3b；
  - **顺带修** `scripts/demo-seed.py` 对 `docs/DEMO-DATASET.md` 的偏离：日期写死成
    2026-07-26/27，而规范明写「D = 拍摄当天」、mac 侧一直是相对实现，导致两端截图
    日期分组对不上。已改为相对今天；
  - **A3**：CI 出的 `AgentTimeline-windows-x64-v0.5.1.zip` 装机验证（sha256 与 Release
    页一致）——托盘常驻（溢出区 UIA 确认）、时间线正常上屏、设置窗 caption
    `Agent Timeline 设置 · v0.5.1`；
  - 冒烟 **354 断言全绿**（含本轮 `ResultExcerptLeadIn()` 6 条）。

- **2026-07-28 (i) 四路解析器对拍 — Windows 侧分叉修复（W-a…W-e）**
  - **W-a codex 摘要器自摄取（高）**：摘要引擎解析到 `codex exec` 时，CliSummarizer 以
    cwd=`%LOCALAPPDATA%\AgentTimeline\summarizer` 起进程，codex 把每条摘要 prompt 写成
    `user_message` 落在 `~\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl`——路径里不含
    "AgentTimeline"/"summarizer"，`SessionWatcher.ShouldIgnore` 的路径级排除完全够不着，
    于是自己发出的每条摘要 prompt 都被当成用户命令收进时间线（自摄取回路）。
    改为按 `session_meta.payload.cwd` 判定（mac 同判据）：`FileContext.Disabled` 置位后
    整文件零事件；流式与重启续扫的 `EnsureMeta` 首行直读走**同一份** `ApplyMeta`；
    claude 侧也补上同判定作双保险；
  - **W-b Claude L1 忽略前缀表（中）**：win 是 9 条且**带闭合 `>`**，mac 是 11 条且匹配
    **裸标签名**。后果：带属性的注入块（`<system-reminder priority="high">`、
    `<bash-stdout exit="0">` 等）前缀匹配不上、整块 XML 变成垃圾"用户命令"节点，且
    `<user_instructions>` / `<environment_context>` 根本不在表里（claude 通道整批漏网）。
    现与 mac `ParserSupport.ignoredPrefixes` 逐字一致；
  - **W-c Claude assistant 多 text 段（低）**：win 在**首段**就 break，首段为空/缺 `text`
    时整条结果行凭空消失。改为拼接全部 `type=="text"` 段（缺 `text` 的段跳过），
    与规范 §1「取其中 type=="text" 段拼接」和 mac 一致；
  - **W-d Claude 项目名跨行沿用（低）**：claude 不是每行都带 `cwd`，win 每行独立回退成
    转义目录 slug（`-Users-x-work-proj`）。改为 per-path `FileContext` 沿用 cwd/项目名；
  - **W-e 时间戳容错（中，双端共同规则）**：两端原本各错一半——mac 解不出丢整行，win
    回退 `DateTimeOffset.UtcNow`（节点跳到时间线顶部装成"刚发生"，且 ts 参与
    `UNIQUE(agent,session_id,ts,command_hash)`，重扫必产生重复行）。新规则：形态照旧宽松
    （`DateTimeOffset.TryParse` 吃的 ISO 变体全收）→ 解不出则**沿用本文件最后一个成功解析
    的时间戳**（进位在每行解析最前面做，任意行解析成功都会更新基准）→ 本文件还没有过任何
    时间戳则丢该行。已落 Claude 与 Codex；zcode 是 win 单端解析器（mac 侧仍是惰性桩），
    暂留旧的 now 回退并就地标注，待 mac 实现 zcode 时一并统一；
  - CoreSmokeTest 266→305 断言全绿（新增 39 条，含"把修复回退即失败"的反证验证）。

- **2026-07-28 (h) Phase C' 双端拉平（W0–W6，mac 侧审计清单全部落地）**
  - **W0 排队命令补录（丢用户命令）**：一轮跑动中键入、被 mid-turn 消费的 prompt
    只剩 `attachment.queued_command` 一份记录，win 此前整类丢弃 attachment 行。
    ⚠ 必须复用同一套 L1 忽略前缀——本机语料 **217 条 queued_command 里 200 条是
    `<task-notification>` 等注入块**，不过滤等于把刚堵掉的 793 次泄漏原路引回；
    净新增真实用户排队命令 17 条。实机重扫验证 claude 节点 81→86；
  - **W1 摘要重试与 attempts 上限**：`nodes.summary_attempts` 幂等 ALTER；失败退避
    1s 会话内重试（此前超时一次就得重启 App），达 3 次停手（此前永久失败节点每次
    启动无上限重跑烧配额）；设置保存清零。引擎不碰 Store，判定经钩子注入；
  - **W2 队列改最新优先**：FIFO Channel → 按 `-ts` 的 PriorityQueue，Channel 退化为
    唤醒信号；回填数百节点时顶部最新节点不再最后才拿到 LLM 标题；
  - **W3 `SetResultLine` 时间戳护栏**：SQL 加 `ts<=$ts`，节点乱序入库时不再把旧回复
    挂到更新的命令上（同文件 `LatestNodeId` 早已如此，此前自相矛盾）；
  - **W4 prompt 注入 agent/project 上下文**：正文骨架与 mac 逐字一致，避免同一命令
    两端得到不同 title/kind；输入上限改走 `DisplayLimits.PromptInput`；
  - **W5 Provider 对齐**：temperature 0.2→0（摘要要可复现）、base URL 自动补 `/v1`
    （不补直接 404）、超时 30s→60s；
  - **W6 `Clip` 改 grapheme 簇口径**：只防代理对不够——ZWJ 家庭、变体选择符、组合字
    都会被从中间劈开；现与 mac `String.count` 同口径；
  - **实机验证附带发现**：`!cmd` 直通 shell 记录以两条节点泄漏进时间线（语料 20 条）
    ——`<bash-stdout>`/`<bash-stderr>` 归入 L1 忽略前缀，`<bash-input>` 转 `$ cmd` 保留；
  - CoreSmokeTest 225→253 断言全绿；`docs/TEXT-NORMALIZATION.md` §4.1 现有 12 条已拉平，
    §4.2 仅剩 mac 侧 zcode 解析器与两条双端待定项。

- **2026-07-27 (g) 实机人值守反馈修复 + zcode 通道点亮**
  - **P1（实机反馈）**：面板内弹层（chip 详情 / 词典 / 右键菜单 / 过滤菜单）是独立窗口化
    popup，打开即夺走激活或触发 PointerExited，主窗被降到 idle 0.25——浮层悬在近透明
    面板上无法阅读。六处弹层统一登记 Opened/Closed，打开期间钉在 hover 不透明度，
    全部关闭且指针不在面板内才回落（实测 242 钉住 / 64 回落）；
  - **P2**：OnNodeAdded 逐条整表重建 O(N²) → 调度队列合并一泵一建；EnsureLoaded
    50 页循环从每页重建收敛为命中后一次；
  - **P3**：摘要 JSON 改平衡候选枚举后向优先（codex stdout 杂讯花括号免疫）；
    标题/关键点/定义截断代理对安全（emoji 不再截出 U+FFFD）；AppSettings.Save
    加锁+原子替换；面板尺寸按 GetDpiForWindow 缩放（已知事项 #6 收敛）；kind 过滤下
    LLM 改判即时增删节点成员资格；
  - **zcode 通道**：用户确认会话在 `~\.zcode\cli\agents`（`sess_*\agent_*\` 每任务一目录）。
    ZcodeParser 按实机样例实现：transcript.jsonl 的 `turn_started.payload.input` → 任务
    命令节点、`turn_complete.payload.response` → 结果行 + 代号挖掘；sidecar metadata.json
    的 cwd → 项目名。默认根随 EnableZcode（默认开）自动监听，设置可覆盖。实机回填
    36 任务节点（hawk-watcher）验证。CoreSmokeTest 90→110 断言。
    ⚠️ `docs/SESSION-FORMATS.md` §4（双端共享）待按报告方案补规范并同步 mac 端解析器；
  - 勘误：(f) 条目所记验证机为 1706x960 @100% 缩放（远程显示），非 150%——DPI 修复
    在本机为恒等变换，高分屏机器生效。

- **2026-07-26 (f) M3 实机验证完成（Win11 Enterprise 26200，全链路首次实机运行）**
  - **实机修复 11 处**（详见当日 fix commits）：种子脚本 UTF-8 BOM（PS 5.1 GBK 误读）；
    分页游标 id-only → (ts,id) 复合（多 agent 回填必丢行，CoreSmokeTest 85→90 断言）；
    watcher 内置 root 预创建 + Error 补扫 + 偏移落库时序；CLI 摘要 prompt 改 stdin
    （.cmd shim 经 cmd.exe 的转义/注入问题，Windows 上 CLI 档原本永远静默降级）+
    超时杀整棵进程树 + 结果信封到手即收针（用户侧 SessionEnd hook 拖住进程不退出）+
    PATH 引号容错；粘性日期头布局后校准（跳跃滚动冻结）；失焦不再改写
    IsInputActive（Acrylic 失焦塌成实心）；托盘 ForceCreate 关 EcoQoS 效率模式；
    托盘退出防僵尸（#5931，Close+Environment.Exit 兜底）；窗口记忆坐标越界回退；
    头部过滤器改紧凑 Button+MenuFlyout（340px 装不下双 ComboBox）+ 标题列可省略。
  - **平台 deviation 终版**：
    1. 粘性日期头为 ViewChanged+布局后校准的模拟实现（mac 原生粘性 section header），
       跳跃滚动下有一拍校准延迟，实测不可感知；
    2. 窗口 hover/idle 渐变实际使用 `opacity.transitionMs`(180ms)，tokens 的
       `hoverFadeMs`(120ms) 仅用于条目内 hover 渐显——与 mac 同源同义；
    3. 头部过滤器为「全部 ▾ / 阶段 ▾」紧凑按钮 + 单选菜单（mac 为 popup 按钮）；
       空间不足时标题省略号让位，长项目名按钮内截断（WinUI 控件 chrome 宽于 mac）；
    4. 「总在最前」在验证机上被系统级拒绝（该会话对一切窗口禁止 topmost，含记事本），
       代码路径正确，需正常交互会话复测——非 app 缺陷；
    5. WinUI Border 描边内缩 9/7px vs mac 8/6px（既有已知项，故意未补偿）；
    6. CLI 摘要 30s 超时对「haiku 被路由到大模型 + 挂 hooks」的重型 claude 配置偏紧，
       靠结果信封提前收针化解；纯 haiku 配置无此问题。
  - 已知未验证收敛：NuGet 版本原样可用；Acrylic 与分层 alpha 在本机 26200 共存正常
    （无需 UseLayeredWindowAlpha 逃生口）；无边框拖动/边缘 resize 命中区实测正确；
    ItemsRepeater DataContext / TemplateSelector 实测正常；托盘图标/菜单实测正常。
    仍未覆盖：Kimi 通道（本机无数据）、provider 真端点、真实鼠标的划选/hover 回执/
    右键菜单（会话环境限制）、单实例保护（已知不做）。

- **2026-07-26 (e) 任意底色自稳对比（对齐 mac 端 PRD §3.2b 末条）**
  - 新增 `color.panelScrim`（浅 #F5F6F7B8 / 深 #14161C8C）：RootGrid 背景改为 scrim 底幕，
    垫在 DesktopAcrylic 材质与全部内容之间——压缩透入底色方差（暗色 IDE/terminal 常态）
    同时保持透光，窗口透明度行为不变；
  - 新增 `color.surfaceStroke`（浅 #0000001A / 深 #FFFFFF24）：命令块与提炼块两级纸面附
    1px 自适应描边（Border BorderBrush/Thickness，圆角不变 3,8,8,8 / 8），同色系底上
    块面自带边界；agent 色墨线仍叠于描边之上（fill → stroke → rule 层序同 mac）；
  - 暗色值调校：commandBg → #2E3542D9、derivedBg → #242A36B4、timelineRail → #454B59、
    entryDivider → #FFFFFF1C、derivedRule → #565D6BA6；
  - Assets JSON 与根 design/ 字节一致，Tokens.xaml（AARRGGBB 换算，Dark 与 Default 同步）
    与 DesignTokens.cs 双色加载表更新。

- **2026-07-26 (d) 提炼块对比度修正（对齐 mac 端同日反馈）**
  - 提炼块落在自己的次级纸面上：新增 `color.derivedBg`（浅 #FFFFFF8C / 深 #242A36A8），
    Border 圆角 8（普通角，无左上压平）、内边距 8×6，14px 缩进保留在纸面之外、虚线墨线
    移入纸面之内；
  - `derivedRule`（浅 #A9AFBB / 深 #4A505E99）与 `dayHeaderRule`（浅 #00000022 /
    深 #FFFFFF26）提亮；元信息行时间与折叠关键点摘要由 textTertiary 升为 textSecondary；
  - Assets JSON 与根 design/ 重新字节一致，Tokens.xaml（AARRGGBB 换算）与 DesignTokens.cs
    双色加载表同步。

- **2026-07-26 (c) "双墨线台账" 时间线视觉重构（对齐 mac 端 PRD §3.2b）**
  - 节点改为无框台账条目：1px entryDivider 细线（越过 22px rail gutter 内缩），需求/决策
    条目附 8% kind 色整条洗染（radius 6）；旧卡片边框/背景与"展开看原文"区块删除；
  - **命令块主角**：原话永远可见（折叠 3 行 / 展开全文），高不透明 commandBg 纸面块
    （CornerRadius 3,8,8,8 左上压平指向 rail），左缘 2px agent 色实线墨线，Cascadia Code
    "❯" 14px 悬挂缩进列，正文 Segoe UI Variable 13.5 SemiBold commandText 可划选；
  - **提炼块**：14px 缩进 + 1px 虚线竖墨线（Line StrokeDashArray 2,3），✦+降级标题
    （命令 ≤20 字或标题为命令归一化前缀重复时隐去），关键点摘要单行 " · " 连接 + accent
    "+n" 计数（展开为完整列表），chips（4px 命中区外扩），绿色结果行；
  - **rail 语法**：每条目连续 2px 轨道段；需求/决策 = kind 色菱形（~9px 旋转矩形），
    任务/修复/调研/学习 = 7px 实心圆，其他/未归类 = 5px 空心圆；定义代号的节点加 accent
    色环（1.5px 描边、2.5px 外扩）；
  - **日期分组**：按自然日分组（今天 · n条 / 昨天 / MM-dd · 周X），条目内嵌分隔行 +
    ViewChanged 驱动的置顶粘性日期条（dayHeaderBg 背衬、CharacterSpacing 120、6px 轨道刻度）；
  - **交互**：整条点击展开（仅背景/元信息行命中，文本划选优先）、chevron 展开旋转 180°、
    hover 浮现 entryHover 背景 + 原话复制按钮（✓ 绿色回执 800ms）、右键菜单
    （复制原话/复制摘要/跳转定义/只看此项目）；动效仅 opacity（hover 120ms 淡入），尊重系统
    UISettings.AnimationsEnabled；
  - tokens 三处同步：Assets JSON 与根 design/ 字节一致（command*/derivedRule/entryHover/
    entryDivider/dayHeader* 色、command/derivedTitle/dayHeader 字号与字距、rail/墨线/缩进
    间距、commandBlock/anchorWash 圆角、marker/lineLimit/glyph/motion 块），Tokens.xaml 与
    DesignTokens.cs 补齐对应资源与解析。

- **2026-07-26 (b) 检测语义对抗性修订（对齐 mac 端同日五处变更）**
  - 定义式正则整体替换：引导符接受冒号/ASCII 逗号/空白、代号可带 `**加粗**`、定义体排除
    顿号与 ASCII 逗号并以负向前瞻在下一个行内 "CODE:" 前截断——行内 "编号如下：N1: 登录,
    N2: 支付"、"- **N1**: xxx"、重放展平的空格分隔列表全部可解析；
  - stopList 归一化存储（去连字符/点后大写比较，`IsStopped`）并扩充技术/规划短码
    （S3/EC2/R2/B2/K8/X86/X64/I18N/L10N/V1–V5/Q1–Q4/H1/H2/P0–P2/MP3/MP4）；新增
    `IsPlausibleName`（2–24 字符、含字母、非停用）闸门 LLM 提取代号（registry 与
    摘要 JSON 解析双侧）；
  - 状态关键词否定检测：关键词前两字符内出现 未没不别无非 则忽略（"尚未完成"/"不执行"
    不再落状态）；
  - ProcessText 自提及排除：本轮定义的代号不参与随后的提及扫描（定义句不是对自身的状态
    更新，define 已计数）；本轮 dash 通道新登记的代号 touch 时 `bumpOccurrence=false`
    不重复计数；
  - 重放标记改为持久化整数 `AppSettings.CodenameReplayVersion`（当前版本 3，存
    settings.json），替代列存在性判断；标记仅在重放**完成后**写入（中途崩溃自动重跑），
    watcher/摘要引擎改为在重放完成回调中启动；
  - CoreSmokeTest 新增 定义式四形态 / 停用词表 / 否定语境 / 定义非自提及 等场景，
    共 85 条断言全部通过。

- **2026-07-26 代号生命周期 + 阶段锚点（对齐 mac 端 PRD §3.3 / §3.3b）**
  - `Core/CodenameDetector.cs`（新增）：与 mac 完全同源的三通道检测——连字符长代号正则、
    `N1: xxx` 定义式（含全角冒号/子句边界）、词典引导短代号精确匹配（ASCII 词边界 +
    子句窗口状态推断 完成/变更/进行中）；
  - `Store`：`codenames` 表迁移（status / status_node / updated / last_context 列）+
    `nodes.kind` / `summaries.kind` 列；`DefineCodename`（最新定义生效，定义改写自动置 变更）/
    `RecordCodename` / `TouchCodename`；`NeedsCodenameReplay` 一次性历史重放标记；
  - `TimelineCoordinator`：agent 回复全文挖掘（TaskComplete.FullText → latest-node 归属）+
    启动时一次性重放 `ReplayCodenamesIfNeeded`；`CodenamesChanged` 事件驱动 chip 徽标刷新；
  - 摘要 JSON 契约升级：`kind`（需求|任务|调研|学习|决策|修复|其他）+ codename `status`
    （定义|进行中|完成|变更|提及）；RuleSummarizer 关键词兜底 `GuessKind`；
  - UI：节点 kind 彩色标签（tokens `color.kind`）、阶段过滤下拉、chip 状态徽标 ✓/△/▶、
    chip flyout 增加状态/最近提及/更新时间、头部代号词典面板（按最近更新排序，点击跳转定义节点）；
  - tokens：`Assets/design-tokens.json` 与根 `design/design-tokens.json` 重新同步
    （新增 `color.statusChanged` 与 `color.kind`），`Themes/Tokens.xaml` 补齐对应资源。

## 环境要求

- Windows 10 1809（build 17763）及以上，推荐 Windows 11（Acrylic 效果最佳）；
- **Visual Studio 2022**（17.8+），安装以下工作负载 / 组件：
  - 「.NET 桌面开发」（.NET desktop development）；
  - 「Windows 应用程序开发」（Windows application development，含 Windows App SDK C# 模板与 Windows 10/11 SDK）；
  - .NET 8 SDK（VS 17.8+ 自带）。

## 打开与运行

1. 用 VS 2022 打开 `windows/AgentTimeline.sln`；
2. 首次打开等待 NuGet 还原（Microsoft.WindowsAppSDK / H.NotifyIcon.WinUI / Microsoft.Data.Sqlite）；
3. 配置选择 **Debug | x64**；
4. F5 直接调试。工程为 **unpackaged**（`WindowsPackageType=None`）+
   `WindowsAppSDKSelfContained=true`，不需要部署 MSIX，也不需要预装 Windows App SDK 运行时。

启动后：

- 悬浮面板出现在主屏右上角（首次运行），可拖动头部区域移动、边缘拉伸改变宽度（280–560）；
- 系统托盘出现图标：左键 显示/隐藏，右键菜单含 显示/隐藏、总在最前、设置、退出；
- 点关闭 / Alt+F4 只是隐藏到托盘，真正退出走托盘菜单「退出」。

## 数据与设置位置

| 内容 | 路径 |
|---|---|
| 设置 | `%LOCALAPPDATA%\AgentTimeline\settings.json` |
| SQLite（节点/代号词典/文件偏移/摘要缓存） | `%LOCALAPPDATA%\AgentTimeline\timeline.db` |
| 日志 | `%LOCALAPPDATA%\AgentTimeline\logs\app.log` |
| CLI 摘要器工作目录 | `%LOCALAPPDATA%\AgentTimeline\summarizer` |

监听的 session 目录（`docs/SESSION-FORMATS.md`，`~` → `%USERPROFILE%`）：

- Claude Code：`%USERPROFILE%\.claude\projects\**\*.jsonl`
- Codex：`%USERPROFILE%\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl`
- Kimi Code：`%USERPROFILE%\.kimi-code\sessions\wd_<项目>_<12hex>\session_<uuid>\agents\main\wire.jsonl`
  （2026-07-28 换代：旧的 `.kimi\sessions` 布局与 TurnBegin/ContentPart 协议已不支持）
- zcode：`%USERPROFILE%\.zcode\cli\agents\sess_<uuid>\agent_<uuid>\transcript.jsonl`
  （默认根自动监听；如需改路径可编辑 settings.json 的 `ZcodeSessionRoot`）

## 设计规范（design tokens）

**`design/design-tokens.json`（仓库根目录）是唯一事实源。**
本工程内 `AgentTimeline/Assets/design-tokens.json` 是它的副本（运行时由 `DesignTokens.cs`
读取透明度/尺寸/agent 颜色），`AgentTimeline/Themes/Tokens.xaml` 是由同一 JSON 手工生成的
XAML 资源（颜色/字号/间距/圆角）。修改 tokens 时请同步三处：
根 JSON → 复制到 Assets → 重新生成 Tokens.xaml（注意 XAML 颜色是 `#AARRGGBB`，
tokens 是 `#RRGGBBAA`，alpha 位置不同）。

## 模块结构

```
AgentTimeline/
├── App.xaml(.cs)               # 组装根：settings/store/registry/engine/coordinator
├── MainWindow.xaml(.cs)        # 悬浮面板：无边框+Acrylic+hover透明度+托盘+时间线 UI
├── SettingsWindow.xaml(.cs)    # 设置界面（F6）
├── DesignTokens.cs             # 解析 Assets/design-tokens.json
├── Themes/Tokens.xaml          # tokens 生成的 XAML 资源
├── UI/
│   ├── TimelineViewModel.cs    # 时间线 VM（倒序、分页、过滤、节点 VM）
│   └── OpacityAnimator.cs      # hover 0.95 / 失焦 0.25，180ms 缓动
├── Interop/
│   ├── WindowInterop.cs        # 分层窗口 alpha + 无边框拖动（Win32）
│   └── FileIdentity.cs         # 文件 fileId（inode 等价物，检测文件重建）
└── Core/                       # 与 mac 端 Core 镜像（namespace AgentTimeline.Core）
    ├── Models.cs               # AgentKind/UserCommand/TaskComplete/Summary/TimelineNode/CodenameEntry
    │                           #   + CodenameStatus/NodeKind（生命周期与阶段标签）
    ├── Store.cs                # SQLite：nodes/summaries/codenames/file_offsets（WAL）+ 生命周期迁移
    ├── CodenameDetector.cs     # 代号检测：长码正则 / 定义式 / 词典引导短码匹配（与 mac 同源）
    ├── CodenameRegistry.cs     # 代号词典：命令+回复+LLM 三路并集，状态机落库与缓存
    ├── SessionWatcher.cs       # FileSystemWatcher + 字节偏移增量 tail + 7 天回填
    ├── TimelineCoordinator.cs  # 数据流编排（watcher→parser→store→engine→UI 事件）
    ├── Parsers/                # Claude/Codex/Kimi 按规范实现；Zcode 占位
    └── Summarize/              # SummaryEngine + Cli/Provider/Rule 三实现
```

## 摘要引擎说明

- 默认「本机 CLI」：调用 `claude -p <prompt> --output-format json --model haiku`
  （PATH 上找不到 claude 时尝试 `codex exec`）；30 秒超时，失败自动降级规则摘要并标记待重试；
- CLI 工作目录固定为 `%LOCALAPPDATA%\AgentTimeline\summarizer`，SessionWatcher 会**忽略**
  该目录产生的 Claude session（防止自我摘要死循环）；
- 「自定义 Provider」：OpenAI 兼容 `/chat/completions`，在设置中填 Base URL / Key / Model；
- 「纯规则」：不调 LLM，首行截断为标题 + 正则提代号。

## 已知未验证事项（在 Windows 上调试时优先检查）

1. **NuGet 版本号**：`Microsoft.WindowsAppSDK 1.5.240627000`、`H.NotifyIcon.WinUI 2.0.131`、
   `Microsoft.Data.Sqlite 8.0.6`、`Microsoft.Windows.SDK.BuildTools 10.0.22621.3233`。
   若还原失败，就近升级到可用版本即可；H.NotifyIcon 2.x API（`TaskbarIcon`/`ForceCreate`/
   `ContextMenuMode="SecondWindow"`）在小版本间偶有变动。
2. **分层窗口 alpha 与 Acrylic 的兼容性**：hover 透明度用 `WS_EX_LAYERED +
   SetLayeredWindowAttributes` 整窗淡入淡出（对应 mac 的 `alphaValue`）。个别 Windows
   版本上分层 alpha 会让 Acrylic 材质失效——若遇到，把
   `UI/OpacityAnimator.cs` 里 `UseLayeredWindowAlpha` 改为 `false`（退化为只淡内容层）。
3. **无边框拖动**：用经典 `WM_NCLBUTTONDOWN + HTCAPTION` 技巧，理论上对 WinUI 3 生效；
   若无效可改用 `AppWindowTitleBar` 拖拽区方案。
4. **ItemsRepeater DataTemplate 的 DataContext**：`ExpandNode_Click` 依赖模板根元素的
   DataContext 为 NodeViewModel（ItemsRepeater 默认行为）；若为 null，改为遍历可视树取绑定项。
5. **Kimi Code wire 协议**：已按本机 44 个真实 session 重写（`turn.prompt` +
   `context.append_loop_event/content.part`），Windows 实机只需确认路径与监听生效。
6. **窗口尺寸 DPI**：tokens 中的面板尺寸按物理像素处理（未乘缩放系数），高 DPI 下面板略小，
   如需精确可乘 `RasterizationScale`。
7. 未做单实例保护（重复启动会有两个托盘图标）。

另：连字符代号正则采用 `\b[A-Z][A-Z0-9]{0,9}(?:-[A-Z0-9]{1,12}){1,3}\b`（与 mac 端
CodenameDetector 同源）——首段量词是 `{0,9}` 而非 `{1,9}`，否则 PRD 自己的示例
`T-PLUGIN-00`（首段单字母 T）无法命中，只会匹配到 `PLUGIN-00`（冒烟测试验证过）。
短码（`N1`/`T2`）只经 `N1: xxx` 定义式或词典引导匹配进入词典，从不裸匹配。

## 与 PRD 的对应

- F1 session 跟踪：`SessionWatcher` + 三个解析器 + zcode 预留 ✅
- F2/F2b timeline 展示：倒序、双墨线台账条目（命令块主角 + ✦ 提炼块 + rail 标记 + 日期
  分组），项目过滤 + 阶段过滤、命令原文常显可划选复制 ✅
- F3 代号词典（含生命周期）：定义式登记 + 词典引导匹配 + LLM 提取三路并集、状态机
  （定义→进行中→完成/变更）、定义重述最新生效、chip 状态徽标与 flyout、词典总览面板、
  历史一次性重放 ✅
- F4 摘要引擎：CLI / Provider / Rule 三实现 + hash 缓存 + 串行限速 + 降级 ✅
- F5 窗口交互：托盘、半透明两档 + 动画、置顶开关、位置尺寸记忆 ✅
  （「非激活面板不抢焦点」为 mac NSPanel 特性，Windows 无直接等价物，未实现）
- F6 设置：引擎/透明度/置顶/回填天数/agent 开关 ✅（版本号在标题栏，双端同串）
