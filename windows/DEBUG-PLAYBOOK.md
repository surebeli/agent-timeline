# Windows 实机调试手册（M3）

> 目标：在 Windows 机器上把「CI 已过编译门禁」推进到「实机运行验证完毕」。
> 按本手册从上到下走，每层验证独立，出问题就地修——CI 会替你守住不回归。

## 0. 环境准备（一次性）

| 项 | 要求 | 备注 |
|---|---|---|
| Visual Studio 2022 | 17.8+，工作负载：**.NET 桌面开发** | WinUI 依赖随 NuGet 恢复，无需 UWP 工作负载 |
| .NET SDK | 8.x | `dotnet --list-sdks` 确认；CI 用 8.0.423 |
| Git | 任意新版 | `git clone https://github.com/surebeli/agent-timeline` |
| Claude Code（可选） | Windows 版 | 摘要引擎默认档；没有就先用「纯规则」或 provider 档 |

命令行等价构建（与 CI 完全一致，可先跑通再开 VS）：

```powershell
msbuild windows\AgentTimeline\AgentTimeline.csproj /restore /p:Configuration=Release /p:Platform=x64 /m
```

> ⚠️ 不要用 `dotnet build`——PRI 打包任务只随 VS msbuild 分发（MSB4062），CI 已踩过。
> ⚠️ 机器若装了 .NET 9/10 SDK，构建目录放一个 global.json 钉 8.x（参考 `.github/workflows/ci.yml` 的 Pin 步骤）。

## 1. 种子数据（无需任何 agent 即可点亮全链路）

首启若 Windows 上从没跑过 Claude Code，时间线会是空的。运行种子脚本伪造一个
符合 `docs/SESSION-FORMATS.md` §1 的 Claude session，覆盖时间线+代号生命周期全场景：

```powershell
powershell -ExecutionPolicy Bypass -File windows\scripts\seed-fixture-session.ps1
```

脚本会在 `%USERPROFILE%\.claude\projects\-fixture-demo\` 写入含以下内容的 session：
需求编号定义（N1/N2/N3）、任务下发（T1/T2）、状态更新（"N2完成""T1 完成，接下去执行T2"）、
长代号（REQ-AUTH-3）、assistant 回复内定义、执行结果文本。
**预期**：启动 app 后时间线出现 6+ 节点，词典登记 N1-N3/T1/T2/REQ-AUTH-3，
N2 ✓完成、T1 ✓完成、T2 ▶进行中；再次运行脚本追加一条 → 观察实时 tail（3 秒内上屏）。

## 2. 分层验证清单

### 2a. 窗口层（挂件行为）
- [ ] 启动后托盘出现图标；主窗为无系统边框、Acrylic 半透明面板
- [ ] scrim 底幕生效：把暗色 IDE/终端放到面板后面，纸面块边界仍清晰（PRD §3.2b 自稳对比）
- [ ] 鼠标移入 → 不透明度升至 ~0.95（120ms 渐变）；移出/失活 → 降至 ~0.25
- [ ] 托盘菜单四项可用：显示/隐藏、总在最前（Topmost 即时生效）、设置、退出
- [ ] 拖动（标题空白区）、边缘 resize、关闭按钮 = 隐藏到托盘（进程不退）
- [ ] 明/暗系统主题切换后重启：两套 token 色板正确（⚠️ 代码构建的画刷是启动时定基调，属已知项）

### 2b. 数据层（watcher/解析）
- [ ] 种子 session 被回填解析（回填窗口默认 7 天）
- [ ] 追加写入 3 秒内增量上屏；app 重启不重复、不丢行（字节偏移持久化）
- [ ] `%LOCALAPPDATA%\AgentTimeline\` 生成 store.sqlite / settings.json
- [ ] 若装了真实 Claude Code：真实会话与种子数据并存显示

### 2c. 台账 UI（对照 mac 截图 docs/assets/screenshot-dark.png 逐项）
- [ ] 指令纸面块：❯ 悬挂缩进、实线 agent 色墨线、圆角 3/8/8/8、1px 描边
- [ ] 派生区：次级纸面 + 虚线墨线（`Line Stretch=Fill StrokeDashArray=2,3` ⚠️ 首验项）
- [ ] rail：连续轴线 + kind 标记（菱形/圆点/空心）+ 定义环
- [ ] 日期分隔：置顶粘性（模拟实现，**快速甩动滚轮**看是否闪烁/滞后 ⚠️）
- [ ] 交互：整条点击展开（划选文本不触发展开 ⚠️ 命中层实现首验）、hover 复制 ✓ 回执、
      右键菜单四项、chips 点击 flyout、词典 flyout、跳转定义节点自动翻页定位
- [ ] `TimelineItemTemplateSelector` 在 ItemsRepeater 上正常出模板（⚠️ 首验项）

### 2d. 摘要引擎
- [ ] 设置 → 纯规则档：节点即时有标题（首句截断）
- [ ] CLI 档（装了 Claude Code）：`claude.cmd` shim 能被解析到；摘要在
      `%LOCALAPPDATA%\AgentTimeline\summarizer` 工作目录运行、词典出现 LLM 定义
- [ ] provider 档：填任意 OpenAI 兼容端点可出摘要；错误时降级规则不崩

### 2e. 性能/边缘
- [ ] 空闲 CPU 近零（任务管理器观察 1 分钟）
- [ ] 500+ 节点滚动流畅（种子脚本带 `-Bulk 500` 参数可灌注）
- [ ] 系统「动画效果」关闭时无动画（UISettings.AnimationsEnabled）

## 3. 已知风险点（先看这里再排障）

按此前平台差异记录（详见 README 更新记录与各条 deviation）：
1. 粘性日期头是 ViewChanged 模拟——极速滚动可能滞后一帧；
2. 代码构建的画刷（CopyBrush/AnchorWashBrush 等）主题定基于启动时；
3. 分层窗口 alpha 与 Acrylic 在个别系统版本上的交互需目验；
4. chip 命中区 Padding/Margin 反向抵消法，相邻 chip 命中区最多重叠 4px；
5. WinUI Border 描边内缩：文字距纸边 9/7px（mac 8/6px），观感差异极小，故意未补偿。

## 4. 修复回路（推荐工作流）

1. Windows 上装 Claude Code / 任意 agent CLI，在仓库根目录开会话——
   `docs/PRD.md`、`docs/ARCHITECTURE.md`、`docs/SESSION-FORMATS.md`、`windows/README.md`
   已含全部上下文，agent 可直接接手修复；
2. 遇到「mac 行为 vs win 行为不一致」时，以 `design/design-tokens.json` + PRD §3.2b/§3.3 为裁决基准；
3. 每次修复 push 后 CI 四道关自动回归（tokens 同源关会拦住忘同步的 token 改动）；
4. 全清单过完 → 在 CHANGELOG 记 0.2.x「M3 实机验证完成」，并把本文件勾选结果留档。
