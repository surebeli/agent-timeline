# Agent Timeline — Windows 端

**中文** · [English](README.en.md)

WinUI 3（Windows App SDK）+ C# / .NET 8 实现的桌面半透明时间线挂件。与 mac 端共享
`docs/SESSION-FORMATS.md` 解析规范、`design/design-tokens.json` 视觉规范与
`design/strings.json` 界面文案。

> ## ✅ 已实机验证完成（最新一轮 2026-07-30）
>
> 本工程最初在 macOS 上编写，**M3 起已在 Windows 11 实机反复编译、运行、验证**，
> 与 mac 端同版本发布。当前状态：
>
> | 层 | 状态 |
> |---|---|
> | 构建 | ✅ VS msbuild x64 Release；CI 六道关（mac swift test / Core 冒烟 / WinUI msbuild / tokens 同源 / 文案表同源 / 演示数据集中英不变式）为硬门禁 |
> | `Core/` + `Interop/` | ✅ 冒烟 **400 断言** 全绿（`windows/CoreSmokeTest/`，任意平台 `dotnet run` 可复跑） |
> | UI 层（XAML / WinUI / H.NotifyIcon / Win32 interop） | ✅ 分层验证清单全项过，逐条注记见 [DEBUG-PLAYBOOK.md](DEBUG-PLAYBOOK.md) §2 |
> | 五条 agent 通道 | ✅ claude / codex / grok / kimi / zcode 均在本机真实语料上跑通 |
> | 发布 | ✅ v0.5.1 安装包实机验证（托盘常驻 / 时间线上屏 / 设置窗版本号） |
>
> **仍未闭环的少数项**（诚实记录，别当已完成）：provider 档未接过
> 真端点；若干「机制已验证、逐帧观感待有人值守复测」的交互项 —— 逐条见下方
> [已知未验证事项](#已知未验证事项对账2026-07-29) 与 DEBUG-PLAYBOOK 中标 ⚠️ 的条目。
>
> 实机调试仍从 **[DEBUG-PLAYBOOK.md](DEBUG-PLAYBOOK.md)** 开始（含种子数据脚本、
> 分层验证清单与宣发截图拍摄规程）。

## 更新记录

- **2026-08-01 代号词典关键字搜索（追齐 mac 同轮）**
  - 判据抽成纯函数 `Core/CodenameSearch.Filter`，**与 mac `filterCodenames` 逐字一致**：
    子串（不是前缀，复合代号 `REQ-AUTH-3` 用户常只记得中间一段）、大小写不敏感、
    匹配范围含 `Definition` 与 `LastContext`（只记得内容不记得代号也能找到）、
    查询词先 trim 且纯空白返回全部。冒烟 451 → **463**；
  - 大小写用 `OrdinalIgnoreCase` 而不是当前区域：避免土耳其语 I 那类"换个系统语言
    搜索结果就变"的坑；`Definition` 在本端可空（mac 是非可空 `String`），判定里单独兜住；
  - 控件选 `AutoSuggestBox` 而非裸 `TextBox`——它自带"有内容才出现的清空 ×"，正是本轮要的；
  - **⚠ 不能设 `QueryIcon`**。模板里查询按钮与清空按钮**占同一个位置**，设了放大镜图标
    就再也不出清空 ×。这不是看代码看出来的：UIA 实测发现设了之后控件内一个 Button 都不
    暴露，去掉才回来。放大镜是装饰，清空是功能，功能优先；
  - 标题计数改成显示**当前可见条数**（不过滤时与总数相等，复用同一个格式串，没加新 key）；
    两种空态分开——`dict.empty`（词典本来就空，此时连搜索框都不摆）与 `dict.searchEmpty`
    （有条目但没搜到）。都显示同一句会让用户以为自己的代号丢了；
  - 搜索词不跨 Flyout 重开保留（每次点开都是全新构建）——这是"随手查一下"的入口，
    不是常驻筛选器；
  - 面板一开就聚焦到输入框，可以直接打字。聚焦挂在搜索框的 `Loaded` 上而不是
    `flyout.Opened`：控件每次开都是新建的，`Loaded` 必然在它真正进入可视树之后触发；
  - 给标题/搜索框/结果区加了 `AutomationProperties.AutomationId`（`DictionaryTitle` /
    `DictionarySearchBox` / `DictionaryResults`）。纯代码构建的控件在 UIA 树里没有锚点，
    本工程没有 UI 测试框架、这类面板的验证全靠 UIA 驱动；
  - **实机验证（真实词典 475 条，UIA 真去打字）**：搜 `C0` → 标题 `Codenames (475)` →
    `Codenames (11)`，11 行与库里预测的 11 条逐条对上；其中 4 条
    （`C2-67`、`ADVISORY-REJECTION-20260726`、`FREEZE-20260722`、`REFREEZE-20260722`）
    **名字里根本没有 C0**，是靠定义/最近提及命中的——匹配范围真的生效；`bc01013e` 命中
    则同时验了大小写不敏感与子串（`b‑c0‑1013e`）。无匹配时标题 `(0)` + `No matching
    codenames`；点清空 × 后回到 `(475)`、输入框清空；重开面板恒为未过滤态；
  - 探针本身坑了三次，记下来免得重来：① 外层 `AutoSuggestBox` **不支持 ValuePattern**，
    可写的是内部 `Edit` 子元素；② 每次过滤都新建结果区元素，开局抓的引用会陈旧、继续
    返回旧内容，每次读之前必须按 AutomationId 重新解析；③ 清空按钮在 **Raw 视图**里
    （模板标了 `AccessibilityView=Raw`），`FindAll` 走 Control 视图怎么找都找不到，
    得用 `TreeWalker.RawViewWalker`——**差点把它当成"按钮没渲染"**；
  - **未实测**：词典整体为空（`dict.empty`）那条分支要求空库，没为它换库；该分支逻辑
    与本轮之前相同，只多了一个提前 `return`。

- **2026-07-31 (ii) 项目名钉在会话启动目录（claude），并回填存量节点**
  - 用户报「消息不及时、时间有问题」：17:06 发的命令在时间线上"显示成上午 9:58"。**时间是对的**
    ——库里那条 `ts` 换算本地正是 17:06:11，与会话文件里的 `2026-07-31T09:06:11.465Z` 分秒不差，
    日志显示 17:06:41 就已在给它跑摘要（落盘一两秒内入库）。**错的是分组**：它被挂到了
    `meeting-hawk`，用户在 `hawk-imuikit-aos-agent` 组里找不到，就把 09:58 那条措辞相近的旧命令
    （原文 `check android 线和 web线`，摘要标题《检查Android与Web测试线》）当成了自己刚发的那条；
  - **根因**：项目名取 `cwd` 末段，而 claude 会话的 cwd 会**漂**——subagent、工具调用里的 `cd`
    都会改写后续行的 `cwd`。实机会话 `8da61f68` 从仓库根启动，cwd 依次漂到 `tools/harness-governance`
    → `uikit_uiautomation_midscene` → `hawk_agent-rs` → `meeting-hawk` → `hawk_server`，
    **一场对话被摊成 7 个"项目"**；全库看同一个仓库裂成 8 组（1428/67/32/15/7/5/2/1）；
  - **改成只认本文件第一条 cwd**（会话启动目录），之后的 cwd 只更新 `ctx.Cwd` 供摘要器自摄取
    判定用。语义变成"按会话在哪儿起的分组"——直接在子目录里起的会话仍然自成一组；
  - **断点续读要回头补读文件头**（`ClaudeParser.FirstCwd` + `PinProjectFromHead`，与
    `CodexParser.EnsureMeta` 同构）。不补读的话项目名会钉在"重启恢复那一刻恰好在哪个子目录"，
    比漂移更难查。头扫描上限 256 KB，不为一个显示名把 20 MB 的会话文件整个读一遍；
  - **只动 claude**：codex 的 cwd 取第一条 `session_meta`（`MetaApplied` 已保证只应用一次），
    grok/kimi/zcode 取目录名，都不存在会话中途漂移；
  - **存量回填**（`TimelineCoordinator.BackfillProjectPins`，marker `ProjectPinBackfillVersion`）：
    解析器改规则只对新节点生效，历史仍是裂的。回填与实时解析共用 `FirstCwd` 一个口径，否则
    回填完再跑一轮又会改回去。只 `UPDATE` project 一列、不碰唯一键，重跑幂等；源文件已删除或
    头部读不出 cwd 的**保持原样，不猜**。放在建窗**之前**同步跑（本机 35 个 claude 源文件，0.84 s），
    晚一步用户这次启动看到的还是旧分组；
  - 冒烟 442 → **451**。实机（真实库，改前已整份备份 db/-wal/-shm/settings）：日志
    `项目归属回填完成：129 个节点改挂到会话启动目录`，8 个碎组并回 `hawk-imuikit-aos-agent`，
    条数 1428+67+32+15+7+5+2+1 = **1557** 与实测逐个相等；那条 17:06 的命令与 09:58、14:23
    两条现在同组。**尚未观测到的一项**：实时 tail 遇到 cwd 漂移的补读路径只有冒烟覆盖，
    真机上要等那个会话下次来新行才会走到。

- **2026-07-31 开机自启动（设置页开关，默认开；追齐 mac 同轮）**
  - **机制选 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 注册表项**，不用
    `Windows.ApplicationModel.StartupTask`：后者是 WinRT API，要求包身份（MSIX）并在包
    清单里声明 `StartupTask` 扩展，而本工程是**非打包**分发（自包含、解压到任意目录直接
    跑 exe）。HKCU（不是 HKLM）不要管理员权限、立即生效；
  - 判据是 `Core/LoginItem.Decide`（纯函数、只用基元类型、10 条断言），副作用在
    `Interop/StartupRegistry.cs`。冒烟 432 → **442**；
  - **路径必须带引号**。默认安装位置就带空格（`…\AppData\Local\Programs\…`），不加引号
    时 Windows 按空格拆出 `C:\Users\foo\AppData\Local\Programs\Agent` 去找，**开机时静默
    失败、没有任何界面反馈**；
  - **比对命令行内容而不只是"注册了没有"**（比 mac 的判据多一档）。非打包分发意味着用户
    随时可能挪目录或换构建，只判有无会在注册表里留下一条指向旧副本的自启动项。反过来
    判等时剥引号 + 忽略大小写，否则历史上不带引号的等价值会被判成"不同"、每次启动白写
    一次注册表；
  - **开关走本窗既有的缓冲式保存，没有照抄 mac 的即时生效**。mac 设置是 `@AppStorage`
    直写、没有"未保存"态，开关一拨就该生效；本端设置窗是改完点「保存」、关窗回滚。
    照抄那个时序会造成"拨了开关又点取消，注册表已经被动过而界面显示没改"——一个用户
    看不见的不一致。现在注册表只在 `Save_Click` 里动；
  - **默认开的语义**：`AppSettings.LaunchAtLogin` 用属性初始化器 `= true`。System.Text.Json
    缺键时保留初始值、显式 `false` 才是关，正是 mac `registerDefaults()` 的等价语义——
    新装用户和从没碰过这项的老用户默认开，**关过它的用户不会被升级重新打开**；
  - **实机六场景**（真实注册表，`reg query` 前后对比）：① 缺键启动 → 写入带引号的正确
    路径；② 值已一致重启 → 判 None、不重复写；③ 注入旧路径 `D:\old\…` 重启 → 重写为真实
    路径并在日志里带出原值；④ 界面关掉点**取消** → 注册表纹丝不动；⑤ 界面关掉点**保存**
    → 项被删除；⑥ 关掉后重启 → 仍是关（没被默认值重新打开）。④⑤⑥ 是 UIA 真去点开关和
    按钮，不是改配置文件模拟；
  - 验证中一次假读数记在这里：场景 ⑥ 我先报了"多出 1 行日志"，是探针把
    `Measure-Object -Line` 当行号用——它比数组元素数少 2（末行无换行符），`Skip` 于是把
    旧行又算了进来。换成 `(Get-Content).Count` 后为 0。

- **2026-07-30 (iii) 滚到底自动翻页，撤掉底部「加载更多」按钮**
  - 用户看它像多余功能。**功能不多余**：`PageSize = 200`，真实库 5455 条，不翻页只能看到
    最新 200 条。多余的是这个**入口形式**——`HasMore` 在大库上恒为真，按钮钉在滚动区之外
    （`Grid.Row=2`），在一个高 340~580dip 的挂件里常驻占掉一行。于是**换掉而不是删掉**：
    `TimelineScroller_ViewChanged` 里判距底 ≤ 120dip 就取下一页，按钮连它那行 `RowDefinition`
    一起撤，`timeline.loadMore` 在本端不再被引用（mac 仍在用，文案表照留）；
  - 判据在 `Core/PanelGeometry.ShouldLoadMore`（纯函数、9 条断言）。三道闸都必要：
    `HasMore` 为假不问库；`loading` 挡重入（`ViewChanged` 一次滚动连发多拍，且取完一页
    内容变长又触发一拍）；`extent ≤ viewport` 时距底恒为 0，不挡会在启动瞬间无条件预取。
    **逐闸变异反证**：拆 `HasMore` 闸→红 1 条、拆 `loading` 闸→红 1 条、拆不足一屏闸→红 2 条；
  - 实机（5455 条真实数据）：逐屏滚到 92.3%，日志 `条目 201 → 402 → 604`，画面正常显示
    翻页取来的 07-27/07-29 旧条目。日志行是按钮撤掉后**唯一可观测的翻页证据**——本工程
    UIA 树跑久了会退化，数不到条目数；
  - **顺带改了重排的套用方式**（`Core/ItemDiff.cs` + `ApplyItems`）。原先每次重排都
    `Items.Clear()` 再整表重加，集合事件数恒等于列表长度。以前无妨——列表通常停在首页
    两百条；现在自动翻页后随手两三千条，而**每来一个新节点都要重排一次**（实时监听器，
    节点持续进来）。改成保住公共前后缀、只动真的变了的槽位。分页正撞"就地型"：首页
    两百条全是今天，翻页后「今天 · n 件」计数一变 `Items[0]` 就对不上、前缀直接归零，
    所以还要有逐槽替换那一档，不能只做前后缀。冒烟 421 → **432**；
  - **查出一个存量缺陷，没修，记在这里**：把滚动条直接拖到最底（或程序化
    `SetScrollPercent(100)`）会让正文变成**稳定空白**（不自愈，小幅回滚也不恢复），
    而几何完全健康（实测 `offset=93862 viewport=853 extent=144268`，偏移没越界、条目都在）。
    是 `ItemsRepeater` 对变高条目估算 extent 的老问题：跳到"估算末尾"会落到真实内容之外。
    **与本轮改动无关**——用 worktree 编了一份改动前的构建对照，点 12 次旧按钮再跳到底，
    空白一模一样。逐屏滚动、跳到 90% 都不受影响。中途我曾把它当成自己引入的回归并"修"了
    一轮，是双版本对照才纠回来的：**先证明探针可信，再判被测对象**。

- **2026-07-30 (ii) 折叠到标题栏（追齐 mac，双端功能对齐）**
  - 头部加第四个图标按钮（chevron，词典之后）：收成只剩标题栏、再点展开回折叠前高度。
    文案走共享表既有的 `header.collapse` / `header.expand`，随状态取；
  - **顶边不动**。mac 是 Cocoa 坐标系（Y 轴向上、原点左下），"顶边不动"写成
    `origin.y += 旧高 - 新高`；Windows 是 Win32 屏幕坐标（**Y 轴向下、原点左上**），
    顶边就是 `Position.Y` 本身——**只改 Height、不碰 Position**。照抄 mac 那行加法会让
    窗口往屏幕下方跳。实机：`2719,345 490x910` → `2719,345 490x57`，X/Y/宽全不变；
  - **折叠态锁住竖向尺寸**。本工程为保留边缘 resize 命中区留了 7px 不可见边框，折叠成
    一条标题栏后**底边那圈就是 resize 区**，不锁的话一拖就能拉高而 `PanelCollapsed`
    仍是 true，标志与实际高度脱钩。WindowsAppSDK 1.5 的 `OverlappedPresenter` 没有
    `PreferredMinimum/MaximumHeight`（更高版本才有），故复用既有的 `OnAppWindowChanged`
    钳制回路（原先只钳宽度）。实机：强行 `SetWindowPos` 拉到 300 被立刻钳回 57；
  - **折叠高度按本端实测、不抄 mac 的 41pt**。几何常量放 `Core/PanelGeometry.cs`，
    运行时以 `HeaderBar.ActualHeight` 为准并与推导值比对，对不上写 `app.log`。
    这道自检当场就抓到我自己写错的常量：按图标按钮推成 40dip，实测 43px——行里最高的
    是带文字的过滤按钮不是图标按钮，已订正为 27；
  - **持久化两个字段** `PanelCollapsed` / `PanelExpandedHeight`。三处坑：
    ① 启动应用折叠态**不走** `SetCollapsed`——那条路会先"记录折叠前高度"，而此刻高度
    已是折叠尺寸，会把真值冲掉（mac 实机踩过，判据是"当前高度 > 折叠高度"才允许记）；
    ② 折叠态下 `SaveWindowBounds` **不写** `WindowHeight`，否则展开高度的第二条线索也没了；
    ③ 老用户升级时 `PanelExpandedHeight` 缺失，取值优先级为
    `PanelExpandedHeight` → `WindowHeight`（仅当它大于折叠高度）→ tokens 默认，最后抬到
    展开最小高度。实机：折叠 → 重启 → 展开，回到 910 且顶边仍在 345；
  - 几何抽成 `Core/PanelGeometry.cs` 的纯函数，**签名只用基元类型**：交接任务书的伪代码
    用了 `RectInt32`，但 `CoreSmokeTest` 是 net7.0、不引 Windows App SDK，那个签名在冒烟
    工程里编不过——「抽纯函数 + 断言」会在写断言时才发现落空。冒烟 400 → **412**；
  - **文案表关加第 7 项**：代码引用的键必须在表里。缺键时加载器**回显键名**、界面上直接
    出现 `header.collapse` 字样而不报错，只有跑起来盯着那个控件才看得见。反证：把
    `header.expand` 打成 `header.expandd` → 精确报出文件与行号。当前扫到 58 个引用键全有定义。

- **2026-07-30 四语接线轮（B 词表 + A 69 键接线 + 一览重拍）**
  - **B 识别词表四语化 + 日韩三条硬伤**（`c72824c`）。否定检测的位置三语不同：中文前置、
    日语后置（完了して**いない**）、韩语两头都有。新增后置否定标记 + 8 字窗口且**遇子句
    边界即止**；韩语前置否定按 **어절**（空格分词）判**不能按字符**——真实语料里
    `이미 완료`(11265)/`제안 완료`(3261)/`잘못`(84805) 都含 안·못·미。`ClauseSeparators`
    补 ASCII 句点但只认**句末形态**（否则 `v0.6.0` 会被从中间截断），向后窗口 24→48
    迁就韩语 SOV。兼容折叠**不用平台 NFKC**：实测 `String.Normalize(FormKC)` 在
    `InvariantGlobalization=true` 下**静默原样返回**（不抛异常），而冒烟工程正是这个配置、
    主程序不是——照搬会让门禁与线上语义不同且无声；理由见
    [docs/TEXT-NORMALIZATION.md](../docs/TEXT-NORMALIZATION.md) §3.6。
    顺带修掉两处一直存在的匹配缺陷：拉丁词无词边界（`prefix`/`suffix` 命中 fix，开发语料
    里几乎无处不在）、中日同形词误伤（`要求`/`判断` 在中文里是高频通用动词，实测把 31 条
    任务误判成需求）。真实语料量化：代号状态判定 **100% 不变**（789 处命中）、类型判定
    97.36% 不变且 137 处变化逐条归因。冒烟 360→**400**，四次反证各自精确打红；
  - **A 69 键接线 + 设置页语言选择器**（`3778ac4`）。界面不再有任何文案字面量。
    落库值与显示标签彻底分开（`UI/UiText.cs`）：`NodeKind`/`CodenameStatus` 落库仍是中文
    rawValue、过滤照旧下推 SQL，语言只换渲染；过滤器的「全部/类型」改用 `::all-projects::`
    哨兵（冒号在 Windows 路径分量里非法，真实项目名不可能相撞），显示时才映射。
    语言**即时生效**，未保存关窗回滚——回滚挂在 `Closed` 而非取消按钮上，标题栏的 X
    不走 Cancel。顺带修掉设置窗保存/取消**被挤出滚动区**（动作条改为钉在 ScrollViewer 外
    常驻）、Windows 空列表无提示两处；
  - **一览三张图按 100% 重拍**（`35a4ac8`）。拍摄脚本改为自己切显示缩放
    （`WindowTool scale set`，走系统「设置」同一条 DisplayConfig 路径）、指针压住面板时
    自己挪开，不再需要人介入。演示配置**钉死 `Language='ZhHans'`**——此前没钉，产出语言
    取决于拍摄机系统 UI 语言（本机 en-US，一跑就拍出英文图而图看着完全正常）；
  - ⚠️ **本轮踩过一次真实数据丢失，两个 bug 各修一处**，教训写在这里：
    ① `try/finally` **挡不住进程被硬杀**——一轮拍摄在"已写入演示库、尚未还原"时被外部
    终止，演示库留在真实位置；下一轮把**演示库当成真实基线**备份，拍完又忠实还原回去，
    三条 ✅ 全打勾而真实数据被水泥封死。**校验通过、数据没了**是最坏的一类失败。
    修法：数据目录立**中断标记**（还原三项全对上才清）+ 固定备份位置 `.shoot-backup`
    + `-Recover` 一键还原；
    ② 上面那道防护**自己又删了一次库**——`finally` 会在 try 里任何位置抛出时跑到还原逻辑，
    包括备份步骤之前；那时备份目录是空的，而还原是"先删真实文件再从备份拷回"：删得掉、
    拷不回，随后启动应用还会重建空库开始回填把现场盖掉。修法：`$swapped` 标志，
    只有真的进入交换阶段才允许还原，否则一个文件都不动。两处都做了反证。

- **2026-07-29 (ii) 单实例保护（补双端分叉）**
  - mac `App/main.swift` 一直有这道闸（发现同 bundle id 的进程就 `exit(0)`），Windows 侧
    从无对应物。面板 `IsShownInSwitchers=false`、无任务栏按钮、收进托盘后完全隐身，
    托盘图标在 Win11 默认还在溢出区——用户想确认它在不在跑，最自然的动作就是**再双击
    一次 exe**，所以这是常规误操作路径而非边缘情况；
  - 实现：新增 `Program.cs` 取代 XAML 生成的 Main（csproj 定义
    `DISABLE_XAML_GENERATED_MAIN`），Main 除首行外与生成版**逐字一致**，位置与 mac 对齐
    ——在任何应用对象存在之前退出，不留半个初始化过的进程；
  - **闸的粒度是一个数据库**，不是一台机器也不是一个登录会话（名字取
    `AppPaths.DatabaseFile` 哈希）：不同用户各有 `%LOCALAPPDATA%` 故互不阻塞（固定名的
    `Global\` 会误伤），同一用户的 RDP + 控制台两个会话共用同一份 store 故仍被拦下
    （`Local\` 前缀拦不住）。Global 建不出来时退到 Local，两者都不行则放行——宁可多一个
    实例也不能让应用起不来；
  - 实机验证：第二个进程 **83ms 退出**（ExitCode 0，赶在 XAML/托盘初始化之前）、托盘只
    剩一个图标、**强杀后可重启**无残留 mutex 死锁、连开三次只剩一个；冒烟 354 断言全绿。

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
    3. 头部过滤器为「全部 ▾ / 类型 ▾」紧凑按钮 + 单选菜单（mac 为 popup 按钮）；
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

界面文案同理：`design/strings.json`（69 键 × 4 语言）是唯一事实源，
`AgentTimeline/Assets/strings.json` 是它的字节一致副本，漂移由 CI 拦下。

## 模块结构

```
AgentTimeline/
├── App.xaml(.cs)               # 组装根：settings/store/registry/engine/coordinator
├── MainWindow.xaml(.cs)        # 悬浮面板：无边框+Acrylic+hover透明度+托盘+时间线 UI
├── SettingsWindow.xaml(.cs)    # 设置界面（F6）
├── AppStrings.cs               # 载入 Assets/strings.json；语言解析与取词
├── DesignTokens.cs             # 解析 Assets/design-tokens.json
├── Themes/Tokens.xaml          # tokens 生成的 XAML 资源
├── UI/
│   ├── TimelineViewModel.cs    # 时间线 VM（倒序、分页、过滤、节点 VM）
│   ├── UiText.cs               # 落库值 → 显示标签的唯一映射点
│   └── OpacityAnimator.cs      # hover 0.95 / 失焦 0.25，快淡入慢淡出
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
    ├── Text/TextNormalizer.cs  # 展示态规整 + 匹配态兼容折叠
    ├── Parsers/                # Claude/Codex/Grok/Kimi/ZCode 按规范实现
    └── Summarize/              # SummaryEngine + Cli/Provider/Rule 三实现
```

## 摘要引擎说明

- 默认「本机 CLI」：调用 `claude -p <prompt> --output-format json --model haiku`
  （PATH 上找不到 claude 时尝试 `codex exec`）；30 秒超时，失败自动降级规则摘要并标记待重试；
- CLI 工作目录固定为 `%LOCALAPPDATA%\AgentTimeline\summarizer`，SessionWatcher 会**忽略**
  该目录产生的 Claude session（防止自我摘要死循环）；
- 「自定义 Provider」：OpenAI 兼容 `/chat/completions`，在设置中填 Base URL / Key / Model；
- 「纯规则」：不调 LLM，首行截断为标题 + 正则提代号。

## 已知未验证事项（对账，2026-07-29）

原「在 Windows 上调试时优先检查」的 7 条，逐条对账如下——**5 条已闭环，2 条仍开**。

| # | 事项 | 现状 |
|---|---|---|
| 1 | **NuGet 版本号**（`WindowsAppSDK 1.5.240627000` / `H.NotifyIcon.WinUI 2.0.131` / `Data.Sqlite 8.0.6`） | ✅ **原样可用**，未做任何升级 |
| 2 | **分层窗口 alpha 与 Acrylic 兼容性** | ✅ 本机 Win11 26200 上**共存正常**，`OpacityAnimator.UseLayeredWindowAlpha` 保持 `true`，逃生口未启用 |
| 3 | **无边框拖动**（`WM_NCLBUTTONDOWN + HTCAPTION`） | ✅ 实测生效；边缘 resize 的 `WM_NCHITTEST` 命中区与宽度钳制一并实测正确 |
| 4 | **ItemsRepeater DataTemplate 的 DataContext** | ✅ 实测正常；`TimelineItemTemplateSelector` 在 4900+ 节点上持续正确 |
| 5 | **Kimi Code wire 协议** | ✅ **已在本机真实语料上跑通**（2026-07-29：120 个 `wire.jsonl` / 177 条回复被正确解析）。此前 DEBUG-PLAYBOOK §2b 记的「本机无 kimi 数据、该通道未覆盖」已不再成立 |
| 6 | **窗口尺寸 DPI** | ✅ 已修：`RestoreWindowBounds` 对 token 尺寸乘 `GetWindowScale`，用户保存的尺寸本就是物理像素原样恢复 |
| 7 | **单实例保护** | ✅ **已实现**（2026-07-29）——`Program.cs` 取代 XAML 生成的 Main，入口处过命名 Mutex 闸，与 mac `App/main.swift` 同一位置同一语义。闸的粒度是**一个数据库**（名字取 `AppPaths.DatabaseFile` 哈希），不同用户互不阻塞、同一用户跨会话仍拦得住 |

另两项不在原 7 条里，一并列明现状：

- **provider 档**：✅ **全链路已通**（2026-07-29，`scripts/provider-check/`）——baseUrl 不带
  `/v1` 时自动补全、Bearer 头、`temperature=0`、解析 `choices[0].message.content` →
  `SummaryJson.Parse` → 落库 `summary_source='Provider'` 且标题被 LLM 值替换，五项判定全绿。
  端点是本机 OpenAI 兼容 mock（真 HTTP、真协议）。
  ⚠ **仍未验**：某个具体厂商端点的响应怪癖。那要用真厂商凭据，不该经手脚本——在「设置」里
  自己填 Base URL / Key / Model，再看 `logs\app.log` 与库里的 `summary_source` 即可。
- **需真实指针驱动的交互项**：2026-07-29 有人值守复测，查出并修好 3 个真实缺陷——
  - ✅ **整条点击展开**：`Tapped` 原先挂在一层夹在中间的透明命中层上，而命令/派生纸面块是
    **不透明 Border、可命中且自身没有处理器**，点在它们身上时事件只往**上**冒泡，那层作为
    兄弟且在下方的命中层永远轮不到 —— 可点区域只剩元信息行与块间窄缝。改挂条目 root
    （对齐 mac `.contentShape(Rectangle()).onTapGesture`）并删掉命中层；
  - ✅ **要点摘要行划选**：折叠态下派生区最显眼的那行，是全条唯一漏了
    `IsTextSelectionEnabled` 的文本；
  - ✅ **派生区右键菜单**：`IsTextSelectionEnabled="True"` 的 TextBlock 会**吞掉右键手势**，
    使其到不了挂在条目 root 的 `Entry_RightTapped`。命令区因为有 ❯ 列、Border 内边距、
    右侧留白等大片非文本像素，右键落在那儿仍能冒泡上去，所以症状表现为「只有命令区
    有菜单」。修法：给条目内各文本加**元素级** `RightTapped`；加上即好，反证了这个判断。
  - ⏳ **仍未复测**：hover 复制 ✓ 回执、快速甩动滚轮的逐帧顺滑度。

另：连字符代号正则采用 `\b[A-Z][A-Z0-9]{0,9}(?:-[A-Z0-9]{1,12}){1,3}\b`（与 mac 端
CodenameDetector 同源）——首段量词是 `{0,9}` 而非 `{1,9}`，否则 PRD 自己的示例
`T-PLUGIN-00`（首段单字母 T）无法命中，只会匹配到 `PLUGIN-00`（冒烟测试验证过）。
短码（`N1`/`T2`）只经 `N1: xxx` 定义式或词典引导匹配进入词典，从不裸匹配。

## 与 PRD 的对应

- F1 session 跟踪：`SessionWatcher` + 五个解析器 ✅
- F2/F2b timeline 展示：倒序、双墨线台账条目（命令块主角 + ✦ 提炼块 + rail 标记 + 日期
  分组），项目过滤 + 阶段过滤、命令原文常显可划选复制 ✅
- F3 代号词典（含生命周期）：定义式登记 + 词典引导匹配 + LLM 提取三路并集、状态机
  （定义→进行中→完成/变更）、定义重述最新生效、chip 状态徽标与 flyout、词典总览面板、
  历史一次性重放 ✅
- F4 摘要引擎：CLI / Provider / Rule 三实现 + hash 缓存 + 串行限速 + 降级 ✅
- F5 窗口交互：托盘、半透明两档 + 动画、置顶开关、位置尺寸记忆 ✅
  （「非激活面板不抢焦点」为 mac NSPanel 特性，Windows 无直接等价物，未实现）
- F6 设置：引擎/界面语言/透明度/置顶/回填天数/agent 开关 ✅（版本号在标题栏，双端同串）
- 四语界面：`design/strings.json`（69 键 × 4 语言）+ `AppStrings.cs` 加载器 + `UI/UiText.cs`
  落库值到显示标签的映射；识别词表四语常开（会话语言与界面语言无关）✅
