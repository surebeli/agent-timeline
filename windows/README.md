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
> 未验证的主要是 UI 层（XAML / WinUI API / H.NotifyIcon / Win32 interop 行为）。

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
    ├── Store.cs                # SQLite：nodes/summaries/codenames/file_offsets（WAL）
    ├── CodenameRegistry.cs     # 代号词典：正则候选 ∪ LLM 提取，首见即定义
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

另：代号正则采用 `\b[A-Z][A-Z0-9]{0,9}(?:-[A-Z0-9]{1,10}){1,3}\b` ——
首段量词是 `{0,9}` 而非 `{1,9}`，否则 PRD 自己的示例 `T-PLUGIN-00`（首段单字母 T）
无法命中，只会匹配到 `PLUGIN-00`（冒烟测试验证过）。

## 与 PRD 的对应

- F1 session 跟踪：`SessionWatcher` + 三个解析器 + zcode 预留 ✅
- F2 timeline 展示：倒序、节点卡片（时间/项目/agent 色点/标题/关键点/代号 chip）、
  展开原文、结果行、项目过滤、全文可划选复制 ✅
- F3 代号词典：正则 ∪ LLM、首见定义、chip 点击弹出定义/出处/跳转 ✅
- F4 摘要引擎：CLI / Provider / Rule 三实现 + hash 缓存 + 串行限速 + 降级 ✅
- F5 窗口交互：托盘、半透明两档 + 动画、置顶开关、位置尺寸记忆 ✅
  （「非激活面板不抢焦点」为 mac NSPanel 特性，Windows 无直接等价物，未实现）
- F6 设置：引擎/透明度/置顶/回填天数/agent 开关/zcode 路径 ✅
