# Windows 实机调试手册（M3）

> 开工 prompt 已备好：[M3-KICKOFF-PROMPT.md](M3-KICKOFF-PROMPT.md)，在 Windows 的 agent 会话中整段粘贴即可。

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
**预期**：启动 app 后时间线出现 **5 个节点**（8 行中仅 user 行建节点；assistant 行只产
resultLine 与代号挖掘），词典登记 N1-N3/T1/T2/REQ-AUTH-3，N2 ✓完成、T1 ✓完成、
T2 ▶进行中、N3 △变更；**用 `-Append` 追加探针行** → 观察实时 tail（3 秒内上屏，实测 0.8s）。
无参重跑 = 删除重建整个 session（全新 fileId 归零重扫），不是追加。

## 2. 分层验证清单

> **M3 实机验证注记（2026-07-26，Win11 Enterprise 26200，1706x960 @100% 缩放）**：本机为远程会话
> （网易UU远程），验证后期远端断开导致：任何窗口无法置顶/取得前台、light-dismiss 弹层
> 开即自散、DWM 停止合成新帧（截图黑帧）。像素通道失效后改用 UIA 树 + DB + 日志取证。
> 标 ⚠️ 的条目为「机制已验证、逐帧观感待有人值守复测」。

### 2a. 窗口层（挂件行为）
- [x] 启动后托盘出现图标；主窗为无系统边框、Acrylic 半透明面板
      ✅ 托盘图标（溢出区）UIA+目视确认；caption=False/thickframe=True 样式实测；Acrylic 材质 hover 态目验
- [x] scrim 底幕生效：把暗色 IDE/终端放到面板后面，纸面块边界仍清晰（PRD §3.2b 自稳对比）
      ✅ 暗色 ZCode 全屏底上命令纸面块边界清晰、半透态下命令仍为最可读元素（截图存证）
- [x] 鼠标移入 → 不透明度升至 ~0.95（120ms 渐变）；移出/失活 → 降至 ~0.25
      ✅ 实测 alpha 64⇆242，ease-out ~180ms（实际用 tokens.opacity.transitionMs=180，本文"120ms"系 hoverFadeMs 笔误）；失活→0.25 亦实测
- [x] 托盘菜单四项可用：显示/隐藏、总在最前（Topmost 即时生效）、设置、退出
      ✅ 四项弹出、显示/隐藏与设置实测可用；⚠️ 总在最前：presenter.IsAlwaysOnTop 调用正确，但本机会话对**一切窗口**（含记事本/自绘 TopMost 窗）系统级拒绝置顶——环境限制非 app bug，需正常交互会话复测；⚠️ 退出：会话后期弹层无法驻留未走完整点击，僵尸修复含 Environment.Exit 兜底（构造性保证），建议人值守复点
- [x] 拖动（标题空白区）、边缘 resize、关闭按钮 = 隐藏到托盘（进程不退）
      ✅ 头部拖动实测位移；resize：WM_NCHITTEST 右/上/下缘 HTRIGHT/HTTOP/HTBOTTOM 全对、宽度钳制 240→280 / 620→560 精确（合成输入起不动原生 NC 拖拽环属环境限制）；WM_CLOSE 与「收进托盘」按钮均=隐藏且进程存活
- [x] 明/暗系统主题切换后重启：两套 token 色板正确（⚠️ 代码构建的画刷是启动时定基调，属已知项）
      ✅ 亮/暗色板重启后均正确（含设置窗亮色底可读）

### 2b. 数据层（watcher/解析）
- [x] 种子 session 被回填解析（回填窗口默认 7 天）
      ✅ 首启回填 4441 节点（种子 5 + 本机 claude/codex 真实 7 天）；词典 294 条，N1定义/N2完成/N3变更/T1完成/T2进行中/REQ-AUTH-3 全部命中
- [x] 追加写入 3 秒内增量上屏；app 重启不重复、不丢行（字节偏移持久化）
      ✅ -Append 探针 0.8s 落库上屏；重启前后 fixture 节点数不变、全库零重复
- [x] `%LOCALAPPDATA%\AgentTimeline\` 生成 timeline.db / settings.json（本文原写 store.sqlite 系笔误）
      ✅ timeline.db(+wal/shm)、settings.json、logs\app.log、summarizer\ 全部生成
- [x] 若装了真实 Claude Code：真实会话与种子数据并存显示
      ✅ claude 119 + codex 4336 节点并存（当时本机无 kimi 数据，该通道未覆盖）
      ✅ **五通道全覆盖补记（2026-07-29）**：库中 claude 158 / codex 4832 / grok 90 /
      kimi 19 / zcode 49 节点并存；kimi 通道另经 `scripts/leadin-diff` 抽取复核——
      120 个 `wire.jsonl`、177 条回复被 `KimiParser` 正确解析。原「kimi 未覆盖」已闭环

### 2c. 台账 UI（对照 mac 截图 docs/assets/screenshot-dark.png 逐项）
- [x] 指令纸面块：❯ 悬挂缩进、实线 agent 色墨线、圆角 3/8/8/8、1px 描边
      ✅ ❯ 列/墨线/纸面块/描边截图目验；⚠️ 圆角逐角像素量测因像素通道失效未做（结构在位）
- [x] 派生区：次级纸面 + 虚线墨线（`Line Stretch=Fill StrokeDashArray=2,3` ⚠️ 首验项）
      ✅ 次级纸面、关键点 " · " 连接行、绿色 → 结果行截图目验；⚠️ 虚线 dash 纹样像素级未量测（审计判定可渲染）
- [x] rail：连续轴线 + kind 标记（菱形/圆点/空心）+ 定义环
      ✅ 轴线/kind 色圆点（任务橙、调研青）/空心圆/accent 定义环截图目验；菱形（需求/决策条目）在真实数据中未遇样本
- [x] 日期分隔：置顶粘性（模拟实现，**快速甩动滚轮**看是否闪烁/滞后 ⚠️）
      ✅（曾 ❌）实机复现跳跃滚动后粘性条以过期几何冻结（该显示时消失/该隐藏时常驻），已修复（ViewChanged 后追加布局后校准）；修后 0%隐藏/40%显示/甩动即时+稳定全过（UIA 驱动）
- [x] 交互：整条点击展开（划选文本不触发展开 ⚠️ 命中层实现首验）、hover 复制 ✓ 回执、
      右键菜单四项、chips 点击 flyout、词典 flyout、跳转定义节点自动翻页定位
      ✅ chevron 展开、chip flyout（代号+状态丸+定义+最近提及+元行）、词典面板（295 条按更新排序）、跳转定义（滚动+展开+徽标 T1✓/T2▶）、加载更多翻页（复合游标）全部实测；⚠️ 整条点击展开/划选不展开/hover 复制✓回执/右键菜单四项：需真实指针+弹层驻留，本会话环境无法驱动，待有人值守复测（右键菜单四项代码与 mac 对应）
- [x] `TimelineItemTemplateSelector` 在 ItemsRepeater 上正常出模板（⚠️ 首验项）
      ✅ 日期头/条目双模板混排在 4900+ 节点上持续正确

### 2d. 摘要引擎
- [x] 设置 → 纯规则档：节点即时有标题（首句截断）
      ✅ 4455 节点 source=Rule 即时标题；GuessKind 关键词兜底正确（调研/任务实测）
- [x] CLI 档（装了 Claude Code）：`claude.cmd` shim 能被解析到；摘要在
      `%LOCALAPPDATA%\AgentTimeline\summarizer` 工作目录运行、词典出现 LLM 定义
      ✅ shim 解析 C:\nvm4w\nodejs\claude.cmd；经两处实机修复（prompt 改 stdin、结果信封到手即收针）后 25s 内 source=Cli 落库、LLM 标题替换规则标题。注：本机 claude 将 haiku 路由至 sonnet-5（CC Switch）且挂 SessionEnd hook，冷启动 >14s，为超时修复的直接诱因
- [x] provider 档：填任意 OpenAI 兼容端点可出摘要；错误时降级规则不崩
      ✅ 假端点（连接拒绝）→ 异常入日志 → 规则兜底 + pending 重试 → 进程存活响应（真端点出摘要未测，无可用测试端点）

### 2e. 性能/边缘
- [x] 空闲 CPU 近零（任务管理器观察 1 分钟）
      ✅ 连续 118 秒采样 CPU 0%（含 EcoQoS 修复后）
- [x] 500+ 节点滚动流畅（种子脚本带 `-Bulk 500` 参数可灌注）
      ✅ -Bulk 500：505 节点 2 秒全部摄取（CPU 尖峰一拍即平），UI 线程 ping 全程 ≤44ms 零阻塞；ScrollPattern 步进/甩动定位精确无卡死；⚠️ 逐帧顺滑度待有人值守目验
- [x] 系统「动画效果」关闭时无动画（UISettings.AnimationsEnabled）
      ⚠️ 代码路径已核（启动时一次性读取，门控条目 hover 渐显），动画有无需真实指针目验，待人值守复测

## 3. 已知风险点（先看这里再排障）

按此前平台差异记录（详见 README 更新记录与各条 deviation）：
1. 粘性日期头是 ViewChanged 模拟——极速滚动可能滞后一帧；
2. 代码构建的画刷（CopyBrush/AnchorWashBrush 等）主题定基于启动时；
3. 分层窗口 alpha 与 Acrylic 在个别系统版本上的交互需目验；
4. chip 命中区 Padding/Margin 反向抵消法，相邻 chip 命中区最多重叠 4px；
5. WinUI Border 描边内缩：文字距纸边 9/7px（mac 8/6px），观感差异极小，故意未补偿。

## 3b. 宣发截图拍摄规程（2026-07-29 在 mac 端定型，Windows 照此复现即两端对齐）

README「实机一览」当前 Windows 那三张仍是四家 agent 时期（v0.4.x）各拍各的比例。
重拍时照下面的参数走，产出与 macOS 三张同画布、同缩放、同背板。

### 铁律（血泪教训，逐条遵守）

1. **隐私红线**：公开截图绝不能出现真实时间线（真实项目名 / 命令原文），
   一律灌注演示数据（`docs/DEMO-DATASET.md` / `windows\scripts\demo-seed.py`）；
2. **数据安全**：拍摄前备份真实 db（含 `-wal`/`-shm`）与设置；换库用**文件级交换**，
   不要删目录（win 端曾因子目录被进程占用导致目录级还原失败）；拍完立即还原，
   并用还原前后 `select agent, count(*)` + 文件 md5 双重核验；
3. **隔离干扰**：演示配置 = 摘要引擎纯规则 + 全部 agent 监听关闭 + 回填 0 天
   （防真实 session 混入、防烧 LLM）；
4. 演示数据在场时间越短越好；任何一步失败**先还原再排障**。

### 参数（与 macOS 三张严格一致）

| 项 | 值 | 为什么 |
|---|---|---|
| 面板尺寸 | **640 × 580 pt/dip** | 640 宽后命令原文不再被词典弹层截断；580 高使收尾正好落在卡片边界而非切半行 |
| 渲染缩放 | **1 pt = 2 px**（mac Retina / win 200%） | 三张里字号、徽标、圆角尺寸才会一致 |
| 合成画布 | **1718 × 1352 px**，内容居中 | 取三态并集（词典态最宽 1526px）+ 96px 四边留白 |
| 背板 | `#101014` + 两处极淡径向光晕 | 与首图、社媒图同一套视觉 |
| 投影 | offset (0, −26)、blur 60、黑 55% | |
| README 显示宽度 | 三列同为 **290** | 同比例 + 同宽 = 高度自动齐平 |

### 三态与抓取方式

| 图 | 状态 | 备注 |
|---|---|---|
| timeline | 无浮层 | |
| projects | 点开「全部」下拉 | 下拉完整落在面板内，无溢出 |
| dictionary | 点开词典弹层 | 弹层以按钮为中心展开，**恒定超出面板右缘 122pt**（与面板多宽无关），故并集比面板宽 |

**抓取必须按窗口、不能按屏幕区域**。mac 用 `screencapture -l <windowId>` 抓窗口自身
缓冲区；Windows 对应 `PrintWindow` + `PW_RENDERFULLCONTENT`（或 Graphics.CaptureItem
按 HWND）。原因：屏幕区域截图会把**盖在上面的第三方全屏浮层**一起摄进来——mac 端实测
有 `UURemoteServer`（layer 1000）与另一个 layer 25 全屏窗口，旧版词典截图上那些彩色
光斑就是它们，不是应用的半透明 bug。窗口抓取与 z 序、遮挡完全无关。

弹层超出面板的那部分背后是空的，会渲染成一块发黑区域并留下接缝——**在合成阶段补背板**，
不要试图用屏幕截图去"接住"它。

### 两个卡死点（都会静默挂住脚本）

1. **下拉/菜单打开时不要走优雅退出**：模态菜单的事件循环会挡住 quit 消息，
   mac 上 `osascript quit` 卡到超时。先关浮层，或直接强杀进程（`pkill -9` /
   `Stop-Process -Force`）——反正设置随后要从备份还原；
2. **自动化 helper 先编译成二进制**：mac 上 `swift file.swift` 每次都重新编译，
   几个交互步骤串起来就超两分钟。Windows 侧同理，别在循环里反复起 PowerShell 编译。

### mac 端参考实现（已入仓，可直接读）

```
macos/scripts/shots/shoot-readme.sh    # 编排：备份 → 灌演示数据 → 三态拍摄 → 合成 → 还原 → 核验
macos/scripts/shots/window-tool.swift  # 窗口枚举 / 合成点击 / 移动指针
macos/scripts/shots/compose.swift      # 合成到统一画布（背板 + 光晕 + 投影）
```

`shoot-readme.sh` 默认只写临时目录便于目验，加 `--install` 才覆盖 `docs/assets/`，
`--hero` 额外拍首图。上表的几何常量就在脚本头部，Windows 侧照抄即可。
两处值得移植的防呆：

1. **落点校验 + 重试**：`FloatingPanel.restoreFrame()` 读不到 `panelFrame` 时会贴主屏右缘，
   而面板贴右缘时词典弹层没地方展开、会被系统挤回面板内，产出尺寸随之改变。
   脚本写完 pref 立刻回读逼 cfprefsd 落盘，并校验实际落点，最多重试 3 次；
2. **不变式拦截**：词典态抓取宽度必须**大于**时间线态——否则说明弹层没能向右溢出，
   直接失败并提示调小 `PANEL_X`，而不是默默产出一套尺寸不一致的图。

已知不可复现处：代号词典按 `updated` 排序，**并列项行序不稳定**（演示数据里 N2/N3、
T1/T2 的 updated 完全相同），两次拍摄的字典图可能行序对调、字节不同。属正常。

### Windows 实机落地（2026-07-29 首次拍成，脚本 `windows/scripts/shots/`）

```
windows/scripts/shots/shoot-readme.ps1        # 编排：校验缩放 → 备份 → 灌演示数据 → 三态 → 合成 → 还原 → 核验
windows/scripts/shots/WindowTool/             # 窗口枚举 / UIA 调用 / PrintWindow 抓取 / 合成（mac 两件套合一）
```

上面那套 mac 参数**大部分照抄，四处必须按 Windows 实机事实改**——都是实测出来的，
照搬 mac 只会得到静默错误的产出：

| 项 | mac | Windows 实测 | 影响 |
|---|---|---|---|
| 弹层归属 | 画在面板窗口缓冲区，**恒定溢出**面板右缘 122pt | WinUI 3 Flyout 默认受 `ShouldConstrainToRootBounds` 约束，被系统**左移挤回面板内**（实测面板 300..940、弹层 576..916） | mac 的「词典态必须比时间线态宽」不变式**在 Windows 上恒不成立**，照抄即误报。换成「调用按钮后面板像素必须有实质变化」，由 WindowTool 硬判 |
| 驱动方式 | 合成点击（CGEvent） | **合成鼠标输入被系统吞掉**：`SendInput` 连指针都挪不动（前后 `GetCursorPos` 完全不变），与 §2a 记的「合成输入起不动原生 NC 拖拽」同源 | 按钮一律走 **UIA `InvokePattern`**，不点坐标 |
| 自动化稳定性 | 一次启动拍完三态 | 面板窗口的 **UIA 树会退化**：跑久了或残留 tooltip 后，后代节点实测从 96 掉到 4，按 `AutomationId` 就找不到控件 | **每一态都重启应用再拍**，顺带清掉上一态残留的弹层 |
| 抓取范围 | `screencapture -l` 取窗口，自带圆角与 alpha | `GetWindowRect` 比可见面板大一圈（四边各 7px 不可见 resize 边框），PrintWindow 把这一圈画成垃圾——顶边一条浅色带、其余三边纯黑；且**不带 DWM 圆角裁剪** | 裁到**客户区**（`GetClientRect`+`ClientToScreen`），圆角在合成阶段按 token `radius.panel=14` 补回 |

两个连带结论：

1. `AppSettings.WindowWidth/Height` 存的是**窗口矩形**，可见面板是客户区。要让可见
   面板正好 640×580dip，得按边框厚度反推窗口尺寸——差值随 DPI 变，脚本在建表那次
   启动时用 `WindowTool border` 量，不写死；
2. 面板宽 640 **不会**被 `panel.maxWidth=560` 钳制：`OnAppWindowChanged` 在
   `RestoreWindowBounds()` 之后才挂上，首次 `MoveAndResize` 不走钳制（用户手动拖拽
   才会被钳到 560）。所以不需要动 `design/design-tokens.json`。

**缩放**：脚本**只校验不修改**显示设置（`-Scale`，默认 200）。改全局缩放会重排机器上
所有开着的窗口，不该由脚本背着人做；不匹配就停下来提示去「设置 → 系统 → 显示 → 缩放」
改。2026-07-29 那轮拍摄机主屏是 100%，故 `-Scale 100`，产出 859×676——画布**宽高比与
dip 几何和 mac 的 1718×1352 逐位相同**（859×676 就是同一组 dip 常量），README 三列同为
290 时两行仍严丝合缝对齐，只是像素密度为 mac 的一半。投影位移/模糊与圆角半径都按
`画布宽 / 859dip` 折算，不是写死像素，换 200% 重拍无需改代码。

## 4. 修复回路（推荐工作流）

1. Windows 上装 Claude Code / 任意 agent CLI，在仓库根目录开会话——
   `docs/PRD.md`、`docs/ARCHITECTURE.md`、`docs/SESSION-FORMATS.md`、`windows/README.md`
   已含全部上下文，agent 可直接接手修复；
2. 遇到「mac 行为 vs win 行为不一致」时，以 `design/design-tokens.json` + PRD §3.2b/§3.3 为裁决基准；
3. 每次修复 push 后 CI 四道关自动回归（tokens 同源关会拦住忘同步的 token 改动）；
4. 全清单过完 → 在 CHANGELOG 记 0.2.x「M3 实机验证完成」，并把本文件勾选结果留档。
