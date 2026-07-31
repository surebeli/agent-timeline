# Windows 同步开工 Prompt（开机自启动轮 · 2026-07-31）

> ## ✅ 本轮已完成（2026-07-31）
>
> | 回报项 | 结论 |
> |---|---|
> | 1 机制选型 | ✅ 按你的建议用 `HKCU\...\Run`，理由与你一致（非打包分发没有包身份，`StartupTask` 走不通）。新增 `Core/LoginItem.cs`（判据）+ `Interop/StartupRegistry.cs`（副作用） |
> | 2 纯判据 + 断言 | ✅ `LoginItem.Decide`，**10 条**断言：你要的三条 + 「期望关且本就没注册 → None」+ 空白值两条 + 路径漂移 + 两条等价写法。冒烟 432 → **442** |
> | 3 保存时序 | ✅ **跟随本端缓冲式保存**，没有照抄 mac 的即时生效。理由见下 |
> | 4 实机验证 | ✅ 六个场景，真实注册表 `reg query` 前后对比，④⑤⑥ 是 UIA 真去点开关和按钮 |
> | 5 CI 六关 | ✅ 全绿（本轮改了 `.cs`/`.xaml` 和 `design/strings.json` 的下游副本，msbuild / dotnet smoke / Strings 三关都实跑到了） |
>
> ### 关于 3（保存时序）——你提醒得对，这里确实是最容易踩偏的一处
>
> 照抄 mac 的即时生效会和本端语义冲突：用户拨了开关又点「取消」，注册表已经被动过、
> 界面却显示没改，而且这个不一致**用户看不见**。现在注册表只在 `Save_Click` 里动，
> 与本窗其它设置项完全同构。实测场景 ④ 专门验了这条：界面关掉 + 点取消 → 注册表纹丝不动。
>
> ### 比你给的判据多做了一档，理由在这里
>
> 你的伪代码是 `Decide(bool desired, bool currentlyRegistered)`，只判有无。我改成比对
> **命令行内容**：非打包分发意味着用户随时可能挪目录或换一份构建，只判有无的话，注册表
> 里会留一条指向旧副本的自启动项，开机静默拉起一个已经不在那儿的 exe。反过来判等时剥
> 引号 + 忽略大小写，否则历史上写过的不带引号的等价值会被判成"不同"、每次启动白写一次
> 注册表。实测场景 ③ 注入 `D:\old\…` 重启，确认被重写成真实路径。
>
> ### 实机六场景（`reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run`）
>
> | 场景 | 注册表 | 设置值 | 日志 |
> |---|---|---|---|
> | ① `settings.json` 里缺键，启动一次 | `<不存在>` → `"F:\…\AgentTimeline.exe"`（带引号） | `<缺键>` | `已写入 "F:\…"` |
> | ② 值已一致，重启 | 不变 | — | **0 行**（判 None，没白写） |
> | ③ 注入 `"D:\old\AgentTimeline\AgentTimeline.exe"`，重启 | 被改回真实路径 | — | `已写入 "F:\…"（原值 "D:\old\…"）` |
> | ④ 界面拨到关 + 点**取消** | **纹丝不动** | 仍 `<缺键>` | 0 行 |
> | ⑤ 界面拨到关 + 点**保存** | → `<不存在>` | → `False` | `已移除注册表项` |
> | ⑥ 关掉后重启 | 仍 `<不存在>` | 仍 `False` | **0 行**（没被默认值重新打开） |
>
> 默认开的语义按你说的做：`AppSettings.LaunchAtLogin` 属性初始化器 `= true`，
> System.Text.Json 缺键时保留初始值、显式 `false` 才是关——等价 `registerDefaults()`。
> 场景 ①⑥ 正是这条的正反两面。
>
> ### 两处如实说明
>
> - **任务书 §4 第一句的方向反了**：写的是"启动时同步一次**系统实际状态到设置值**"，
>   但括号里的做法（读注册表、算 `Decide`、按需写）和 mac `LoginItem.sync` 都是
>   **设置值 → 系统**。我按括号里的做法实现（偏好是事实源）。附带后果：用户若手工删掉
>   注册表项而设置仍是开，下次启动会被重新写回——这一点与 mac 行为一致。注意任务管理器
>   「启动」页的禁用**不删值**（它写在 `StartupApproved\Run` 下的另一处），所以我们不会和
>   任务管理器打架。
> - **验证过程中我报过一次假读数**：场景 ⑥ 我先说"多出 1 行日志"，实为探针把
>   `Measure-Object -Line` 当行号——它比数组元素数少 2（末行无换行符），`Skip` 把旧行又算
>   了进来。换成 `(Get-Content).Count` 后为 0，日志时间戳也印证期间没有写入。
>
> ### 本轮不做的三条都遵守了
>
> 没有带参数启动、没碰 MSIX 打包、没改根 README 能力表。另：**没有升版本号、没有发
> release**——你上一轮直接发了 0.7.1（我的任务书里写的是不发），这轮我按自己任务书的
> "本轮不做"执行，0.7.2 要不要发等用户定。
>
> 下面 `---` 之间是本轮的原始任务书，保留备查。

> 用法：在 Windows 机器的仓库根目录启动 agent 会话，把下面 `---` 之间的整段粘贴为首条指令。
>
> 上一轮（演示数据集日韩语料核实）已完成，见 git log 里本文件的上一版
> （`463e8f9`）。v0.7.1 已发布（分页改滚到底自动加载 + 设置窗标题 i18n 修复），
> 随后 mac 侧又修了一个真 bug：托盘菜单「退出」被自动置灰、且没有 Cmd+Q（`1b9c632`，
> 还没跟着发版，等你这轮做完再一起看要不要发 0.7.2）。本文件整体替换为本轮内容。

---

你在一台 Windows 11 机器上，当前目录是仓库 agent-timeline 根目录
（远端 github.com/surebeli/agent-timeline，先 `git pull` 到最新 main）。产品是双端桌面
挂件「Agent Timeline」，两端跑在 CI 六道关下，最新发布 v0.7.1。

**本轮任务：双端支持开机自启动，设置页面加一个开关，默认打开。** 这是用户直接提的新需求
（原话「mac 和 windows 支持开机自启动，设置页面增加设置项，默认支持开机自启动」），不是
从任何任务书来的，也不是 scope creep——记在这里备查。

mac 侧我已经做完、实机验证过、push 了；你这边需要独立实现 Windows 的等价机制，
不是照抄 mac 代码（两端的系统机制完全不同，见下）。

## mac 侧做了什么（供你参照判断，不代表你要照抄实现细节）

- **机制**：`SMAppService.mainApp`（`ServiceManagement` 框架，macOS 13+ 起注册主 app
  本身不需要额外的 helper bundle/LoginItems target，是当前推荐做法——`LSSharedFileList`
  / `SMLoginItemSetEnabled` 都已废弃）。新文件 `macos/Sources/AgentTimeline/App/LoginItem.swift`。
- **纯判据抽出来单独测**：`LoginItem.action(desired: Bool, current: SMAppService.Status) -> Action`
  （`.register` / `.unregister` / `.none`），因为系统实际状态会跟应用存的偏好脱钩——
  用户可能在系统设置的登录项列表里手动关掉，不能假设两边总一致，也不该无条件重复调用
  register/unregister。这条只测纯函数，不测真实的 `SMAppService.register()` 调用
  （SPM 测试二进制不是正经 .app bundle，`SMAppService` 对它的行为没有保证）。
- **默认值**：`AppSettings.registerDefaults()` 里把 `launchAtLogin` 注册成 `true`——
  `UserDefaults.register(defaults:)` 只在**从没显式存过这个键**时才生效，新装用户和
  升级上来、从没碰过这项设置的老用户都适用，正好对应「默认打开」这个要求。
- **应用时机**：app 启动时调一次 `LoginItem.sync(desired: AppSettings.launchAtLogin)`，
  把系统实际状态同步到偏好；设置面板里的开关 `.onChange` **立即**调用（mac 设置是
  `@AppStorage` 直写模型，没有"未保存"状态，开关本身就该即时生效，不能等一个「应用」
  按钮——拖到点应用才同步，开关显示状态和系统实际状态会有一段对不上）。
- **实机验证**（用 `sfltool dumpbtm` 读系统的 Background Task Management 记录，这是
  Apple 官方诊断工具，能看到任意 app 的登录项 disposition，不只是自己）：
  - 全新安装（没写过 `launchAtLogin` 键）启动一次 → BTM 记录里 AgentTimeline 出现，
    `Disposition: [enabled, allowed, notified]`；
  - 设置面板把开关关掉 → 立即变成 `[disabled, allowed, notified]`；
  - 再打开 → 变回 `[enabled, allowed, notified]`，`Generation` 计数器同步递增。
  - 三步都是通过 UI 实际点出来的（System Events 驱动菜单/勾选框），不是只测代码逻辑。

## 共享层已经改完，你只需要 pull

- **`design/strings.json`**：新增 `settings.launchAtLogin` 键（四语齐全），紧跟在
  `settings.alwaysOnTop` 后面：

  ```json
  "settings.launchAtLogin": {
    "zh-Hans": "开机自启动",
    "en": "Launch at login",
    "ja": "ログイン時に自動起動",
    "ko": "로그인 시 자동 실행"
  }
  ```

- **`windows/AgentTimeline/Assets/strings.json`**：已同步复制（字节一致，`check-strings.py`
  本地跑过全绿，72 键 × 4 语言）。

## Windows 侧要做的：机制完全不同，请自己设计，别照抄 mac

### 1. 登录项机制

Windows 侧**不要用 `Windows.ApplicationModel.StartupTask`**——那是 WinRT API，正常只对
**打包（MSIX）**app 生效，需要包清单里声明 `<StartupTask>` extension 才能用。这个 app
是**非打包**分发（`windows/README.md` 说得很清楚：自包含 Windows App SDK，解压到任意
目录直接跑 `.exe`），没有包身份，`StartupTask` 这条路大概率走不通（就算某些 SDK 版本
能绕过去，也是非标准用法，不建议）。

推荐用最朴素、对非打包 exe 最可靠的机制：**`HKEY_CURRENT_USER\Software\Microsoft\Windows\
CurrentVersion\Run` 注册表项**。开：写一个值（名字随你定，比如 `AgentTimeline`）指向
`Process.GetCurrentProcess().MainModule.FileName`（**运行时动态取自己的路径**，不要写死——
用户解压到哪个目录你不知道）；关：删掉这个值。HKCU（不是 HKLM）不需要管理员权限，
立即生效，没有 mac 那边"可能需要用户去系统设置批准"的中间态——这点上 Windows 反而更简单。

### 2. 抽个纯判据，参照 mac 的 `LoginItem.action` 但用你们自己的类型

mac 那条纯函数测的是"给定期望状态 + 系统当前状态，该不该调用"。Windows 建议同构：

```csharp
// Core/ 下，签名只用基元类型，好在 CoreSmokeTest 里断言（同折叠轮 W-1 的教训）
public enum LoginItemAction { Register, Unregister, None }

public static LoginItemAction DecideLoginItemAction(bool desired, bool currentlyRegistered)
{
    if (desired && !currentlyRegistered) return LoginItemAction.Register;
    if (!desired && currentlyRegistered) return LoginItemAction.Unregister;
    return LoginItemAction.None;
}
```

`currentlyRegistered` 的取值来自读注册表（`Registry.CurrentUser.OpenSubKey(...).GetValue(...)
!= null`）。副作用调用（读写注册表）本身不用测，跟 mac 一样靠实机验证。

### 3. 设置面板的开关：注意你们是缓冲式保存，跟 mac 不一样

**这条是本轮最容易踩偏的一处，请仔细判断**：mac 的设置是 `@AppStorage` 直写模型，开关
一拨就立即生效，没有"未保存"状态。Windows 的设置窗**是缓冲式的**（改了要点"保存"、
关窗有回滚——上一轮任务书原文提过"Windows 那套设置窗的缓冲式保存/关窗回滚"）。

如果照抄 mac 的"开关一拨就立即调用注册表写入"，会跟你们自己的保存/取消语义冲突：
用户拨了开关又点"取消"，注册表已经被动过、界面却显示没改——这跟你们其他设置项的
"取消真的不生效"行为不一致。**建议**：这颗开关跟你们别的设置项走同一条保存路径——
只有真正点了"保存"（或等价的确认动作），才去调 `DecideLoginItemAction` + 实际写注册表；
点"取消"关窗则连界面状态一起回滚，跟别的设置项完全同构。如果你判断 Windows 侧其实
也适合做成"即时生效"（比如这颗开关有正当理由跟别的设置分开），也可以，但**必须说明
理由**，不要因为图省事复制 mac 的时序就带过去。

### 4. 默认值与应用时机

- 设置的持久化层（无论是 `settings.json` 还是别的）里，`LaunchAtLogin` 这个字段**默认
  `true`**——新装用户和从没碰过这项的老用户都要落到默认打开，跟 mac 的
  `registerDefaults()` 语义对齐（"从没显式存过这个键时才用默认值"，不是每次读取都强制
  true）；
- app 启动时同步一次系统实际状态到设置值（参照 mac `LoginItem.sync` 的思路：读当前
  注册表状态、算 `DecideLoginItemAction`、按需写）；
- UI 文案用共享表的 `settings.launchAtLogin`（`AppStrings.S("settings.launchAtLogin")`
  或你们的等价写法），别再手写一遍。

## 需要你确认并回报的事项

1. 登录项机制选型与理由（应该是 Run 注册表项，除非你有更好的理由选别的）；
2. 纯判据函数 + `CoreSmokeTest` 断言（对齐 mac 三条：期望开但没注册 → Register；
   期望关但已注册 → Unregister；已经一致 → None）；
3. 设置面板的保存时序怎么处理的（跟随缓冲式保存，还是你判断该即时生效——如实说明）；
4. 实机验证：全新状态启动一次，确认注册表 Run 项被写入且指向正确路径（含空格路径要
   加引号）；设置里关掉，确认注册表项被删除；再打开，确认恢复。**贴实测结果**（比如
   `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的输出前后对比），
   不要只说"应该没问题"；
5. Windows 侧现有 6 道 CI 关是否受影响（这轮新增了 `design/strings.json` 一个键，
   Strings sync 关必然要跑一次；`.cs`/`.xaml` 改动会过 msbuild 和 dotnet smoke 关）。

## 本轮不做

- 不要求"开机自启动"支持带参数启动到特定状态（比如强制展开/折叠）——跟正常双击启动
  行为完全一致就够了，这颗功能不改变启动后的界面状态逻辑；
- 不涉及 MSIX 打包（如果将来想让 Windows 也走 `StartupTask` 那条路，前提是先打包分发，
  是完全独立的、大得多的话题，不在本轮范围）；
- 不改根 README 的核心能力表（这是个设置项级别的小功能，双端都做完再一起判断要不要
  加一行说明，本轮先不加）。
