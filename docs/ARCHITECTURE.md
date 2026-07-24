# Agent Timeline — Architecture

## 平台结构

```
agent-timeline/
├── design/design-tokens.json     # 双端共享视觉规范（唯一事实源）
├── docs/                         # PRD / 架构 / 解析规范
│   └── SESSION-FORMATS.md        # 各 agent session 文件格式规范（双端解析器共同依据）
├── macos/                        # Swift Package（SwiftUI + AppKit）
│   ├── Package.swift
│   ├── Sources/AgentTimeline/
│   └── scripts/build-app.sh      # 组装 AgentTimeline.app
└── windows/                      # WinUI 3 (C# / Windows App SDK)
    └── AgentTimeline/
```

## mac 端模块

```
Sources/AgentTimeline/
├── App/
│   ├── main.swift                # NSApplication 引导（无 @main storyboard）
│   ├── AppDelegate.swift         # 状态栏、面板生命周期、模块装配
│   └── AppSettings.swift         # UserDefaults 包装（透明度/置顶/引擎/agent 开关）
├── Core/
│   ├── Models.swift              # AgentKind / UserCommand / TimelineNode / Codename / Summary
│   ├── Store.swift               # SQLite（系统 libsqlite3，无三方依赖）
│   └── CodenameRegistry.swift    # 代号词典（正则候选 ∪ LLM 提取，首见即定义）
├── Watch/
│   └── SessionWatcher.swift      # FSEventStream + 每文件字节偏移增量 tail + 回填扫描
├── Parsers/
│   ├── AgentSessionParser.swift  # 协议：canHandle(url) / parse(newData, context) -> [SessionEvent]
│   ├── ClaudeParser.swift
│   ├── CodexParser.swift
│   ├── KimiParser.swift
│   └── ZcodeParser.swift         # 预留：路径可配，格式待样例
├── Summarize/
│   ├── SummaryEngine.swift       # 协议 + 调度（串行队列、缓存命中、降级链）
│   ├── CLISummarizer.swift       # claude -p / codex exec headless，严格 JSON 输出
│   ├── ProviderSummarizer.swift  # OpenAI-compatible /chat/completions
│   └── RuleSummarizer.swift      # 无 LLM 兜底
└── UI/
    ├── DesignTokens.swift        # 解析 design-tokens.json（编译进 bundle）
    ├── FloatingPanel.swift       # NSPanel(nonactivating) + NSVisualEffectView + hover 透明度
    ├── TimelineView.swift        # SwiftUI 时间线（倒序、懒加载、划选复制）
    ├── NodeViews.swift           # 节点卡片 / 代号 chip / 展开态
    └── SettingsView.swift        # 设置窗口
```

## 数据流

```
FSEvents ──> SessionWatcher ──(增量字节)──> Parser(按 agent) ──> SessionEvent
                                                    │
                       UserCommand / TaskComplete   ▼
                                              Store (SQLite)
                                                    │  未摘要的命令
                                                    ▼
                                             SummaryEngine ──缓存──> Store
                                                    │
                                   NotificationCenter / @Observable
                                                    ▼
                                              TimelineView
```

- **SessionEvent** 归一化：`userCommand(text, ts, project, sessionId, agent)` / `taskComplete(ts, sessionId, lastAgentMessage)`；
- 解析线程与 UI 线程隔离；Store 为唯一写入点（WAL 模式）；
- 摘要异步补全：节点先以 RuleSummarizer 结果即时上屏，LLM 摘要完成后原位刷新。

## 关键机制

### 增量 tail
每个被跟踪文件在 SQLite `file_offsets` 表记录 `(path, byte_offset, inode)`。FSEvents 触发后 seek 到偏移读新数据，按行解析（半行留缓冲）。inode 变化视为文件重建，从 0 重读。

### 摘要 JSON 契约（CLI 与 provider 共用）
```json
{
  "title": "≤20字标题",
  "keyPoints": ["关键点/需求点/任务点，每条≤30字"],
  "codenames": [{"name": "T-PLUGIN-00", "definition": "该代号在本命令中的含义"}],
  "resultLine": "agent 完成情况一句话（若已知）"
}
```
CLI 调用形如：`claude -p --output-format json --model haiku <prompt>`，失败或超时（30s）自动降级 RuleSummarizer 并标记待重试。

### 窗口行为（mac）
- `FloatingPanel: NSPanel`，`styleMask: [.nonactivatingPanel, .titled(隐藏), .resizable, .fullSizeContentView]`；
- `level = settings.alwaysOnTop ? .floating : .normal`；
- `NSTrackingArea` mouseEntered/Exited 驱动 `animator().alphaValue`（hover 0.95 / 失焦 0.25，设置可调）；
- `collectionBehavior: [.canJoinAllSpaces, .fullScreenAuxiliary]`；
- 文本用 SwiftUI `.textSelection(.enabled)`。

### 双端统一
`design/design-tokens.json` 定义色板（light/dark）、字号阶、间距、圆角、透明度两档、动效时长。mac 编译期打进 bundle；win 端以同一文件生成 XAML 资源。系统特有材质各自保留（NSVisualEffectView vs Mica/Acrylic）。

## Windows 端（M3 scaffold）

WinUI 3 + Windows App SDK（C#）。`H.NotifyIcon` 托盘；`AppWindow` + `OverlappedPresenter.IsAlwaysOnTop`；`DesktopAcrylicController` 半透明；`PointerEntered/Exited` 驱动 opacity 动画；解析器按 `docs/SESSION-FORMATS.md` 实现（与 mac 相同规范）；session 路径为 `%USERPROFILE%\.claude\projects` 等对应位置。
