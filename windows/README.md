# Agent Timeline — Windows 端（M3 scaffold）

WinUI 3（Windows App SDK）+ C# / .NET 8 实现的桌面半透明时间线挂件。与 mac 端共享
`docs/SESSION-FORMATS.md` 解析规范与 `design/design-tokens.json` 视觉规范。

> ⚠️ **重要：本工程在 macOS 上编写，尚未在 Windows 上编译运行过。**
> 代码按可编译标准编写，但请预期少量琐碎修正（NuGet 版本号、个别 API 签名等）。
> 详见下方「已知未验证事项」。
>
> ✅ 已验证部分：`Core/`（解析器/Store/词典/摘要引擎/协调器）与 `Interop/` **不依赖 WinUI**，
> 已在 macOS 用 .NET SDK 实际编译通过（0 警告 0 错误），并对三个解析器的过滤规则、
> 代号正则、SQLite 读写/去重/偏移表、摘要 JSON 契约跑过功能冒烟测试（全部通过）。
> 冒烟测试工程在 `windows/CoreSmokeTest/`（独立 console 工程，未挂进 .sln），
> 在仓库任意平台 `dotnet run` 即可复跑。
> 未验证的主要是 UI 层（XAML / WinUI API / H.NotifyIcon / Win32 interop 行为）。

## 更新记录

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
- Kimi：`%USERPROFILE%\.kimi\sessions\<hash>\<uuid>\wire.jsonl`
- zcode：预留（在设置中填 session 根目录后启用；解析器为占位实现）

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
5. **Kimi TurnEnd payload**：规范未定义字段，代码做了 best-effort 提取，拿到真实样例后修正。
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
- F6 设置：引擎/透明度/置顶/回填天数/agent 开关/zcode 路径 ✅
