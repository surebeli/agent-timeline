# Windows 同步开工 Prompt（折叠功能移植轮 · 2026-07-30）

> ## ✅ 本轮已完成（2026-07-30，`c5e9e6c`）
>
> 折叠/展开落地，CI 六道关全绿，冒烟 400 → **412**。交付清单逐条：
>
> | # | 交付项 | 结论 |
> |---|---|---|
> | 1 | 功能落地 + CI 六关全绿 | ✅ `c5e9e6c` |
> | 2 | 几何纯函数 + 8 项断言；函数落 `Core/` 且签名不带 WinUI 类型 | ✅ `Core/PanelGeometry.cs` |
> | 3a | 合法帧校验：折叠高度的矩形不被判废 | ✅ 手工把 `WindowHeight=57` 写进设置再启动，坐标原样保留在 `2719,345`——mac 那类「矮就被当垃圾、连位置一起丢」在 Windows 不成立 |
> | 3b | 应用折叠态不冲展开高度 | ✅ 冒烟断言 + 实机「折叠→重启→展开」回到 910 |
> | 4 | W-2 折叠态竖向锁 | ✅ 强行 `SetWindowPos` 拉到 300 被立刻钳回 57 |
> | 5 | W-3 升级路径优先级 + 断言 | ✅ |
> | 6 | W-5 两版 README 核心能力表加行 | ✅ |
> | 7 | 命中区检查结论 | ✅ **已检查，本轮不动**，见下 |
> | 8 | `windows/README.md` 更新记录 | ✅ |
> | 9 | 本文件标记完成 | ✅ 即本节 |
>
> ### 3a 顺手炸出一个真缺陷（已修）
>
> 做那条验证时发现：`PanelCollapsed=false` 但 `WindowHeight=57` 时，窗口**真就起成 57px 高**。
> 因为竖向钳制挂在 `AppWindow.Changed` 上，而 `RestoreWindowBounds` 跑在**订阅之前**——
> 存量或被手工改过的设置里一个畸小值不会被任何回路救回来。已在还原处补一次钳制，
> 复测 `57 → 320` 且坐标不动。
>
> ### 命中区（第 7 项）：已检查，结论是本轮不动
>
> 实测四个头部图标按钮均为 **20×20 dip**（`WindowTool rect`，@100%）；mac 修完是 21×26pt。
> 也就是说 Windows **竖向比 mac 修完后还小 23%**，结构上确实是同一类「命中区约等于字形」
> 的问题。没动的原因是**改它会连锁**：放大 `HeaderIconButtonStyle` 的 Padding → 头部行高变
> → 折叠高度变（`PanelGeometry` 常量要跟着改）→ **三张 README 一览图要重拍**。
> 本轮任务书写明这条「不强制」，且 Windows 侧无人反馈过难点，故留到独立一轮连重拍一起做。
>
> 下面 `---` 之间是本轮的原始任务书与接收方 review，保留备查。

> 用法：在 Windows 机器的仓库根目录启动 agent 会话，把下面 `---` 之间的整段粘贴为首条指令。
>
> 上一轮（四语接线轮）已全部完成并对账，历史见 git log（`3778ac4`/`c72824c` 起）。
> 本文件整体替换为本轮内容。

---

你在一台 Windows 11 机器上，当前目录是仓库 agent-timeline 根目录
（远端 github.com/surebeli/agent-timeline，先 `git pull` 到最新 main）。产品是双端桌面
挂件「Agent Timeline」，两端跑在 CI 六道关下。

**本轮任务：把 mac 端新加的「折叠到只剩标题栏」功能搬到 Windows。** 这不是接线，是一个
mac 已经落地、Windows 完全没有的产品功能——`grep -rn "collapse" windows/` 只命中无关的
`Visibility.Collapsed`。

## 必读（动手前读完）

1. `macos/Sources/AgentTimeline/UI/FloatingPanel.swift` 的 `collapsedFrame` /
   `setCollapsed` —— 参照实现，含两处实机踩出来的 bug 和为什么这么修的注释；
2. `macos/Tests/AgentTimelineTests/PanelCollapseTests.swift` —— mac 侧断言覆盖点，
   你在 Windows 侧要覆盖同样的场景；
3. `windows/AgentTimeline/MainWindow.xaml.cs` 的 `RestoreWindowBounds` /
   `SaveWindowBounds` —— 你自己的窗口几何持久化范式，本任务要在这个范式里加两个字段，
   不要另起一套。

## 任务 A：折叠/展开功能

### 交互

头部三个图标按钮（词典 / 设置 / 隐藏）旁边加第四个：一个 chevron，点一下把窗口收成
只剩标题栏的高度，再点一次展开回收起前的高度。mac 是把它放在词典按钮之后、原有其他
按钮之前；Windows 侧按你实际的头部控件顺序插入即可，不必位置死抠一致。

文案键已经在共享表里了，**不用你加**：`header.collapse`（"折叠到标题栏"）/
`header.expand`（"展开"），四语齐全，`windows/AgentTimeline/Assets/strings.json`
已经是最新副本（`design/strings.json` 加键时一并再生成过）。接线时直接
`AppStrings.S("header.collapse")` / `AppStrings.S("header.expand")`。

### ⚠️ 坐标系方向相反，这条最容易搬错

mac 用 Cocoa 坐标系，**Y 轴向上**、原点在屏幕左下；折叠时"顶边不动"的实现是
`origin.y += (旧高度 - 新高度)`——原点 Y 变大，因为窗口在向上收。

Windows 用 Win32 屏幕坐标，**Y 轴向下**、原点在屏幕左上——你熟悉的
`AppWindow.Position.Y` 正是这个坐标系。"顶边不动"在你这边的正确实现是
**`Position.Y` 保持不变，只改 `Size.Height`**（窗口从底边往上收，顶边原地不动）。
如果照搬 mac 那行代码的"加法方向"，折叠后窗口会跳到屏幕下方去——这不是防御性提醒，
是这两个坐标系天生反着来，必须重新推导，不能抄公式。

建议做法：把几何计算抽成一个纯函数（参照 mac `collapsedFrame` 的做法），只接收/返回
`RectInt32`（或你们惯用的几何类型），不碰 `AppWindow`——这样能在 `CoreSmokeTest` 里
断言，不需要起真实窗口：

```csharp
// 伪代码，按你们的实际类型调整
static RectInt32 CollapsedFrame(RectInt32 current, bool collapsed, int expandedHeight)
{
    var target = collapsed ? CollapsedHeight : Math.Max(ExpandedMinHeight, expandedHeight);
    return current with { Height = target };   // Position.Y 不动——win32 坐标系顶边即原点
}
```

### 折叠高度：不要抄 mac 的 41pt，量你自己的

mac 的 41pt = 它自己头部布局的 `padding(top:4) + frame(height:28) + padding(bottom:8) +
分隔线 1`。Windows 头部是 `Padding="12,10,12,6"` 起手的自绘布局（`RowDefinition
Height="Auto"`，不是写死高度），两边字号、内边距都不同，41 这个数字在你们这边没有意义。

做法：在真实窗口里量 `HeaderBar`（`Grid.Row="0"` 那个）的 `ActualHeight`，折叠目标高度
就是它（可能还要加边框/阴影的量，实测为准）。**这条必须有断言**：写一个能在没有真实
窗口时也验证「折叠高度确实等于头部实际高度」的用例——如果头部布局以后改了，这条要跟着
打红，而不是留着一个过期的写死数字（参照 mac
`testCollapsedHeightMatchesHeaderLayout`，它验的是「折叠高度 == 头部各段 padding 之和」，
不是验一个孤立数字）。

### 持久化：两个新字段，两个真实 bug 要避开

mac 侧 `AppSettings` 加了 `panelCollapsed`（bool）、`panelExpandedHeight`（折叠前的高度，
单独存，不能只指望 `WindowHeight` 字段——折叠后那个字段存的就是折叠尺寸了）。
Windows 侧在 `Core/AppSettings.cs` 同样加两个字段，走你们现有的 `Save()` /
`JsonSerializer` 范式即可。

mac 实机测出两个 bug，**Windows 的实现形态不同，不会原样复现，但请你按下面两条各写
一个反证用例，确认你们没有同构的问题**：

1. **「合法帧」的校验阈值要放宽到能容纳折叠高度**。mac 的 `restoreFrame()` 原来判
   `height > 100` 才采信保存值，41pt 被当垃圾数据，退回默认分支、连位置一起丢。
   你们 `RestoreWindowBounds()` 现在的校验是 `DisplayArea.GetFromRect(...)` 相交检查，
   形状不同、大概率不会因为「矮」被拒——但请显式确认：折叠态存下的
   `RectInt32(x, y, width, 折叠高度)` 拿去做这个相交校验，结果依然是「合法」。
   写个用例喂一个折叠高度的矩形进去，断言校验通过；
2. **应用折叠态时不能把真实展开高度冲成折叠高度**。mac 的坑是：启动时如果上次是
   折叠的，会调用「折叠」这个动作本身，而那个动作的实现里"顺手"把**当前高度**记成
   "折叠前高度"——可当前高度已经是折叠尺寸了，于是把真实值覆盖掉。**判据**：
   只有当"当前高度 > 折叠高度"时才允许把它记为展开高度；启动时如果发现设置里
   `panelCollapsed=true`，走的应该是"直接应用折叠尺寸"，不能经过"记录展开高度→折叠"
   这条会覆写的路径。写一个用例：设展开高度=600，模拟"当前已是折叠尺寸"的状态，
   断言展开高度设置**没有**被改写成折叠高度。

### 命中区（顺手检查，不是本轮强制项）

用户反馈过 mac 的图标按钮（原本约 11pt 字形）很难点到，mac 侧把四个图标按钮的命中框
统一放大到 21×26pt（同一手法：固定 `frame` + `contentShape`，**不是** padding 正负抵消，
后者在 SwiftUI 里对命中测试无效，具体见 mac `TimelineView.swift` 里 `iconHit` 那段注释）。

Windows 的 `HeaderIconButtonStyle`（`App.xaml:27`）现在是 `Padding="4"` +
`MinWidth="0"`/`MinHeight="0"`，12px 图标——结构上是同一类"命中区约等于字形本身"的问题，
但**没有人实测过 Windows 端是否真的难点**（WinUI 的点击容差、DPI 缩放都可能让实际体验
不一样）。本轮不强制处理，若你顺手实测发现确实难点，可以一并放大 `Padding`；
如果不动，请在报告里说明「已检查，结论是 XX」，不要跳过不提。

## 接收方 review 补充（Windows 侧 2026-07-30 读完任务书 + 对照 mac 实现与本端现状后补）

> 以下五条是**我的 review 结论**，不是用户决定。前四条会让执行者踩空或让任务书的核心
> 要求落空，请与上面的正文同等对待；第五条是文档真实性问题，需要决定怎么处理。

**W-1 · 伪代码里的 `RectInt32` 在冒烟工程里编不过 —— 这条会让「抽纯函数 + 断言」整条落空**

任务书要求「几何抽成纯函数并在 `CoreSmokeTest` 里断言」，给的伪代码签名是
`static RectInt32 CollapsedFrame(RectInt32 current, ...)`。但 `CoreSmokeTest.csproj` 是
**`net7.0`（不带 `-windows`）、只引 `Microsoft.Data.Sqlite`、只编译
`../AgentTimeline/Core/**/*.cs` + `Interop/FileIdentity.cs`** ——
`Windows.Graphics.RectInt32` 来自 Windows App SDK 投影，那里拿不到。

照伪代码写，会在写完断言时才发现编不过，然后大概率退化成「函数留在 `MainWindow.xaml.cs`
里、断言算了」。要求改成：

- 纯函数**放在 `windows/AgentTimeline/Core/` 下**（否则不在冒烟工程的 glob 里，比如
  `Core/PanelGeometry.cs`）；
- 签名**只用基元类型**（`(int x, int y, int w, int h)` 或 Core 本地的小 record），
  `RectInt32` 的转换留在 `MainWindow` 一侧。

**W-2 · mac 锁了折叠态的竖向尺寸，任务书没把这条列成 Windows 要求**

mac `setCollapsed` 里显式写了 `minSize.height = maxSize.height = collapsedHeight`，
注释给的理由是**否则 `collapsed` 标志会与实际高度脱钩**。任务书让读 `setCollapsed`，
但没把这条提成 Windows 的必做项，而 Windows 这边**更容易踩**：

本工程为了保留边缘 resize 命中区，特意留了 7px 的不可见边框（`WindowTool border`
实测「左上偏移 7,7 / 总宽高差 14×14」）。折叠成 ~41px 高之后，**底边那 7px 就是 resize
区**——用户随手一拖就能把折叠态拉高到 300px，而 `panelCollapsed` 仍是 `true`。下一次点
chevron 会按"展开"走，或者更糟：把 300 记成"折叠前高度"。

Windows 侧的对应机制是 `OverlappedPresenter.PreferredMinimumHeight` /
`PreferredMaximumHeight`（`MainWindow` 已经持有 `_presenter`，见 `InitializeWindowChrome`）。
折叠时把两者都设成折叠高度、展开时放开。**这条要有实机验证**：折叠后试着拖底边，
确认拖不动。

**W-3 · 老用户升级路径没定：`panelExpandedHeight` 缺失时展开高度从哪来**

任务书的 bug 2 说的是「折叠时不能把展开高度冲成折叠高度」，方向是**写**。但还有一个
**读**的缺口：已有用户升级上来时，`settings.json` 里有 `WindowHeight`、没有
`panelExpandedHeight`。mac 那边是靠 `AppSettings` 的默认值兜住的（它自己的注释里提到
「被 AppSettings 的回退兜住了」）。

Windows 要显式定优先级，建议：`panelExpandedHeight` 缺失（0）时，取 `WindowHeight`
——但**仅当它大于折叠高度**（否则说明用户上次退出时正处折叠态，那个值是折叠尺寸），
再不行取 tokens 的 `PanelDefaultHeight`。这条也该有断言。

**W-4 · 实时钳制不会打回折叠（已核实，不用担心）**

顺手核过 `OnAppWindowChanged`：它**只钳制宽度**，且 `Resize` 时原样带回
`size.Height`。所以折叠不会被这个回路救回去。任务书没提，这里记一下免得执行者去猜。

**W-5 · 共享的根 README 现在展示了一个 Windows 没有的功能**

任务书把引导图列为「mac-only 文档改动、不需要 Windows 配一套」——**作为拍摄任务**这没错，
但那两张图是内联在**双端共享**的根 `README.md` / `README.en.md` 里的，Windows 用户也在看，
而 `onboarding-2-collapse` 演示的正是折叠。核心能力表里也**没有**折叠这一行。

也就是说：从 mac 那个 commit 起到 Windows 补齐之前，README 在向所有人演示一个只有一半平台
有的能力，且没有任何说明。两条路选一条：

- 本轮把 Windows 补齐，README 不用改（推荐——反正就是本轮任务）；
- 或者在补齐前，README 那节加一句「折叠目前仅 macOS」。

顺带：折叠是个用户可见能力，**补齐后核心能力表该加一行**（两版 README 一起加）。

**W-6 · 交付清单漏了 mac 的第 8 项断言**

清单说「参照 mac `PanelCollapseTests` 的 8 项」，但列出来的短语只有 7 个覆盖点。
mac 实际的 8 个 `func test` 里，漏掉的是 **`testCollapseStringsExist`**——校验
`header.collapse` / `header.expand` 两个键真的在表里。

我已核实这两个键在 `windows/AgentTimeline/Assets/strings.json` 第 65 / 71 行，副本同源
（`check-strings` 71 键通过）。但仍该有断言：文案表将来再生成时漏了键，界面会**回显键名**
而不报错，只有跑起来才看得见。

## 执行规则

- 独立 commit（中文 commit message，风格参考 `git log`）；
- 几何计算抽成纯函数并在 `CoreSmokeTest` 里断言（当前 400 条），新增覆盖点参照 mac
  `PanelCollapseTests` 的 8 项：顶边不动 / 展开还原高度 / 往返无损 / 异常展开高度兜底 /
  折叠高度与头部布局同步 / 展开高度设置的回退 / 两个 bug 的回归用例；
- `msbuild ... /restore` + `dotnet run --project windows/CoreSmokeTest -c Release` 全绿；
- 实机验证：折叠 → 展开 → 重启 → 展开，位置/宽度/高度全部核对，**不要只说"应该没问题"**，
  截图或逐条描述实测数值；
- `design/strings.json`、`docs/` 等双端共享层**本轮不需要改**（键已就位）；如果你发现
  真的需要改，先停下来报告方案；
- 阶段性 push，CI 六道关自动回归。

## 本轮不做（可选，别自己加进任务书）

- **README 新手引导截图**：mac 侧已加两张聚光标注图（`docs/assets/onboarding-*.png`，
  中英各一套），是 mac-only 的文档改动，不需要 Windows 侧配一套。如果你们将来想要
  Windows 版的同类引导图，那是独立的、低优先级的任务，本轮不涉及；
- 命中区放大（见上，检查但不强制）。

## 最终交付

1. 折叠/展开功能落地并 push，CI 六道关全绿；
2. 几何纯函数 + 断言，覆盖点对齐 mac `PanelCollapseTests` 的**全部 8 项**（含 W-6 那条
   文案键存在性）；纯函数须落在 `Core/` 下、签名不带 WinUI 类型（W-1）；
3. 两条反证用例的结果（合法帧校验 / 应用折叠态不冲展开高度）；
4. **W-2 折叠态竖向尺寸锁**：实机拖底边确认拖不动，贴实测数值；
5. **W-3 升级路径**：`panelExpandedHeight` 缺失时的取值优先级 + 断言；
6. **W-5 README**：折叠补齐后在两版核心能力表各加一行；若本轮未补齐，说明 README 怎么处理；
7. 命中区检查结论（处理了还是没处理，为什么）；
8. `windows/README.md` 更新记录追加本轮条目；
9. 本文件顶部标记本轮完成，或更新为下一轮内容；
10. 汇报：完成项 / 实机验证数据 / 新发现问题 / 与 mac 行为的任何不一致点。**如实报**——
    对不上就说对不上，不要凑。

## 已知分叉（本轮不做，仅供参照）

- **§4.2 第 20 条：mac 无摘要缓存**。mac `Store.swift` 只建
  `nodes`/`codenames`/`file_offsets` 三张表，win 另有 `summaries`；PRD §3.4 在 mac 端
  从未实现。将来补实现时**绝不能把语言混进命令 hash**（它参与唯一键，改了重扫必出
  重复行）；
- §4.2c Grok 编排器派发的任务书过滤——需先定双端规范；
- `timeline.unpin` 键：mac 头部有置顶切换按钮，Windows 没有该控件，故 Windows 侧
  不引用这个键，属有意留着；
- provider 档未接真厂商端点；hover 复制回执与快速甩动滚轮的逐帧顺滑度仍待人值守复测。
