# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/) 与语义化版本。

> **发布流程**：改根目录 `VERSION` + 本文件新增条目 → commit 推 main（常规 CI 全绿）→
> `git tag vX.Y.Z && git push origin vX.Y.Z` → Release 工作流自动校验
> tag↔VERSION↔CHANGELOG 一致性、跑双端测试、出 macOS `.app` zip 与 Windows x64 zip
> 并挂到 GitHub Release。

## [0.7.6] - 2026-08-01

代号词典加关键字搜索（双端）；外加一条 v0.7.5 自产回归的热修。

### 新增（双端）

- **代号词典支持关键字搜索**。用户直接提的需求（原话「对于字典需要增加个需求，就是能
  关键字搜索字典，比如搜N1，能定位到字典」）。面板打开即聚焦、输入即过滤、不需要回车，
  有内容时出现清空按钮；搜索词**不跨面板重开保留**——这是「随手查一下」的入口，不是常驻
  筛选器，所以不做成设置项。标题计数改为显示**当前可见条数**（不过滤时与总数相等）。
  两种空态分开：词典本来就空（此时连搜索框都不摆出来）与「有条目但没搜到」——都显示同
  一句会让用户以为自己的代号丢了。
  - **匹配语义两端逐字一致**（mac `TimelineViewModel.filterCodenames`、
    Windows `Core.CodenameSearch.Filter`）：**子串而非前缀**（复合代号 `REQ-AUTH-3`
    用户常只记得中间一段）、匹配范围含**定义与最近提及**（只记得内容、不记得代号叫 N1
    还是 N2 也能找到）、大小写不敏感、查询词先 trim 且纯空白视为没有搜索词（返回全部）。
    判据两端都抽成纯函数并单测，不散落在 UI 事件处理里；mac `swift test` 92 → 98，
    Windows 冒烟 451 → 463；
  - Windows 侧用 `AutoSuggestBox`，但**不能设 `QueryIcon`**——模板里查询按钮与清空按钮
    占同一个位置，设了放大镜图标就再也不出清空 ×。UIA 实测发现的：设了之后控件内一个
    Button 都不暴露；
  - 两端都用**真实词典**验证「匹配范围真的生效」，而不只是测最简单的名字匹配：mac 搜
    `N1` 命中 3 条里有 2 条靠最近提及命中；Windows 在 475 条真实词典上搜 `C0` 得 11 条，
    与直接查库预测逐条相同，其中 4 条名字里根本没有 `C0`。

### 修复（macOS）

- **摘要器自己的会话泄漏进了可见时间线**（v0.7.5 自产回归，发现即修）。0.7.5 新加的
  文件头扫描把 `context.cwd` 提前填好了，于是「`cwd` 变了才检查是不是摘要器 scratch
  目录」这条判据永远不触发——摘要器 headless 会话的 `cwd` 从第一行起就是 scratch 目录、
  全程不变，根本没有「变化」这个事件。真实库里已观察到污染：摘要器的自问自答以
  `project="summarizer"` 进入时间线，且 app 开着就每隔几秒新增一条（发现时 39 条），
  并传导进代号词典（两个子虚乌有的代号，以及一个真实代号的 occurrences 被吹高、状态
  被误置）。修法是头部扫描扫到 scratch 目录当场 `disabled`；另加两条 version-gated
  一次性迁移清理已受影响的库（按 `cwd` 精确匹配删除泄漏节点 + 代号词典从干净的节点
  历史整体重建）。实机：删除 39 个节点与污染数量精确对应，重跑幂等，`swift test` 98 → 100。
  **Windows 不受影响**（实测 0 条泄漏）：本端头部扫描自己就做了摘要器判定，且只在断点
  续读时才跑；`SessionWatcher` 另有一层路径级排除。

## [0.7.5] - 2026-07-31

修一个把同一场对话摊成好几个「项目」的分组缺陷。用户报的现象是「消息不及时、时间有问题」——
实际时间戳分秒无误，是命令被挂到了别的项目组里、在原来那组找不到，于是把一条措辞相近的旧命令
当成了自己刚发的那条。Windows 先落地，macOS 复现确认后同轮跟进对齐。

### 修复（Windows）

- **claude 会话的项目名钉在「会话启动目录」**，不再跟着 `cwd` 漂。原规则取每条记录 `cwd` 的
  末段目录名，而一场会话里的 `cwd` 会被 subagent、工具调用里的 `cd` 改写：实机会话从仓库根
  启动后 `cwd` 依次漂到 `tools/harness-governance` → `uikit_uiautomation_midscene` →
  `hawk_agent-rs` → `meeting-hawk` → `hawk_server`，**一场对话被摊成 7 个「项目」**，同一个
  仓库在全库里裂成 8 组。改后只认本文件第一条 `cwd`，之后的漂移只用于「是不是摘要器自己的
  会话」判定。语义是**按会话在哪儿起的分组**——直接在子目录里起的会话仍然自成一组；
- **断点续读回头补读文件头**（`ClaudeParser.FirstCwd` + `PinProjectFromHead`，与
  `CodexParser.EnsureMeta` 同构）。否则项目名会钉在「重启恢复那一刻恰好在哪个子目录」，
  比漂移更难查。头扫描封顶 256 KB，不为一个显示名整读 20 MB 的会话文件；
- **一次性回填存量节点**（`TimelineCoordinator.BackfillProjectPins`，marker
  `AppSettings.ProjectPinBackfillVersion`）。解析器改规则只对新节点生效，历史仍是裂的。
  回填与实时解析共用 `FirstCwd` 同一口径，否则回填完再跑一轮又会改回去；只 `UPDATE project`
  一列、不碰唯一键，重跑幂等；源文件已删除或读不出 `cwd` 的保持原样，不猜。在建窗**之前**
  同步跑完，否则本次启动看到的还是旧分组；
- 其余通道不受影响：codex 的 `cwd` 取第一条 `session_meta`（本就只应用一次），
  grok / kimi / zcode 取目录名，都不存在会话中途漂移。

### 验证（Windows）

- Core 冒烟 442 → **451**：漂移不改组、续读补读文件头、`FirstCwd` 在缺文件时返回 null，
  以及回填的三条（改对、源文件没了不动、重跑幂等）；
- 真实库实机（改动前整份备份 db/-wal/-shm/settings）：`项目归属回填完成：129 个节点改挂到
  会话启动目录`，8 个碎组并回一组，条数 1428+67+32+15+7+5+2+1 = **1557** 与实测逐个相等；
  35 个 claude 源文件回填耗时 0.84 s，本次启动零 WARN / 零 ERROR。

### 修复（macOS）

- 同一缺陷，独立复现确认后对齐：**本会话自身的真实语料**里 `cwd` 就漂过——
  `agent-timeline` → `agent-timeline/macos` → `.../Sources/AgentTimeline` →
  `agent-timeline/windows`，回填前真实库对应节点分落 `agent-timeline\|85`、`macos\|2`、
  `AgentTimeline\|1` 三组。规则与验证同 Windows（项目名钉在会话启动目录、断点续读补读
  文件头封顶 256 KB、存量回填 version-gated 一次性迁移），实现路径不同：
  `ClaudeParser.firstCwd` 头部扫描 + `ParsedFileContext.projectPinned` 兜底、
  `AppDelegate.backfillProjectPinsIfNeeded`。顺手把项目名派生换成早已存在但从未被这条
  路径调用过的 `ParserSupport.projectName(fromCwd:fallback:)`（统一走反斜杠归一化+空白
  兜底，与 win `ParserUtil.ProjectNameFromCwd` 同口径）。只改 claude：复核确认 codex/
  grok/kimi/zcode 都不存在中途漂移。

### 验证（macOS）

- `swift test` 95 → **98**（漂移不改已钉住的项目名、`firstCwd` 忽略漂移只认文件头、
  `makeContext` 靠头部扫描钉住——覆盖断点续读场景）；
- 真实库实机（改动前整份备份 db/-wal/-shm/defaults）：回填日志
  `项目归属回填完成：9 个节点改挂到会话启动目录`，9 = `macos(2)+AgentTimeline(1)+
  harnessloop(5)+android(1)` 逐项对上；claude 节点总数回填前后不变（226 → 226，只是
  重新分组）；项目筛选下拉里四个幽灵组消失；手工清零 marker 强制重跑确认幂等
  （结果逐字段相同、无新变更）。

## [0.7.3] - 2026-07-31

macOS 快捷键轮：补齐 Cmd+W，去掉托盘菜单里两个从来没生效过的假标签。

### 修复（macOS）

- **Cmd+W 无响应**：设置窗聚焦时按 Cmd+W 该关窗、主面板聚焦时按 Cmd+W 该等价隐藏，
  此前两者都毫无反应。根因与 0.7.2 的「退不掉」是同一类——accessory 策略的 app 没有
  `NSApp.mainMenu`，标准系统快捷键没有任何菜单条目可以承载。补了本地按键监听，按当前
  谁是 key window 分发：设置窗是 key 就 `performClose`，面板是 key 就 `orderOut`。
- **托盘菜单「显示/隐藏」的 ⌘T 标签摘掉**：这个标签从加上以来就没有生效过——状态栏菜单
  （`NSStatusItem.menu`）上的 `keyEquivalent` 只是显示标签，只有装进 `NSApp.mainMenu`
  的菜单项才会被系统当全局快捷键处理，状态栏菜单从来没有这个资格。与其留一个好看但不
  work 的标签，不如摘掉。
- **顺手把「设置…」的 ⌘, 也接上真动作**：同一处的同一类问题，且 Cmd+, 是 macOS 上打开
  偏好设置的标准约定，一并用本地按键监听补上，不再是摆设。

以上均用 System Events 实际驱动菜单点击/按键验证（面板聚焦按 Cmd+W → 面板从窗口列表
消失；打开设置窗按 Cmd+, 能唤出、再按 Cmd+W 能关掉；托盘菜单截图确认标签与实际生效的
快捷键一致），不是只过了单元测试。

## [0.7.2] - 2026-07-31

开机自启动轮：双端支持开机自启，设置页新增开关、默认打开；修一个 macOS 侧长期存在的
「退不掉」bug。

### 新增（双端）

- **开机自启动**，设置页新增开关，**默认打开**。用户直接提的需求。两端机制不同，各自按
  平台的推荐做法实现，不是互抄代码：
  - **macOS**：`SMAppService.mainApp`（macOS 13+ 起注册主 app 本身，不再需要 helper
    bundle；`LSSharedFileList` / `SMLoginItemSetEnabled` 均已废弃）。开关**即时生效**——
    mac 设置是 `@AppStorage` 直写模型，本就没有「未保存」状态。用 `sfltool dumpbtm` 读系统
    的 Background Task Management 记录实机验证：全新安装启动一次即出现
    `[enabled, allowed, notified]`，关掉变 `[disabled, …]`，再开变回来；
  - **Windows**：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 注册表项。**不用**
    `Windows.ApplicationModel.StartupTask`——那是 WinRT API，要求包身份（MSIX）并在包清单
    里声明扩展，而本端是非打包分发（自包含、解压到任意目录直接跑 exe）。开关**跟随本端
    既有的缓冲式保存**（改完点「保存」才生效、点「取消」什么都没发生），不照抄 mac 的即时
    生效——那会造成「拨了开关又点取消，注册表已经被动过而界面显示没改」这种用户看不见的
    不一致。判据是纯函数 `Core/LoginItem.Decide`（10 条断言，冒烟 432 → 442），并且**比对
    命令行内容而不只是「注册了没有」**：非打包分发下用户随时可能挪目录或换构建，只判有无
    会留下一条指向旧副本的自启动项。写入的路径**必须带引号**——默认安装位置就带空格，
    不加引号时 Windows 按空格拆开去找，开机时静默失败且没有任何界面反馈。六个场景在真实
    注册表上验过（含「拨了点取消 → 纹丝不动」「关掉后重启 → 没被默认值重新打开」）;
  - 两端「默认打开」的语义一致：**只有从没显式存过这个偏好时才用默认值**（mac
    `UserDefaults.register(defaults:)` / Windows 属性初始化器 + System.Text.Json 缺键保留
    初始值），所以主动关掉过的用户不会被升级重新打开。

### 修复（macOS）

- **应用退不掉**：托盘菜单「退出」点不动、Cmd+Q 也没反应（v0.7.1 用户上报，实为从加这个
  状态栏菜单起就一直存在的 bug）。`rebuildStatusMenu()` 末尾把每一项的 `target` 都强制指向
  `AppDelegate`，包括 action 为 `NSApplication.terminate(_:)` 的退出项；`AppDelegate` 不实现
  这个方法，AppKit 的菜单自动校验一发现 target 不响应就把菜单项置灰——退出从来没真的生效
  过。退出项现单独 `target = NSApp`。Cmd+Q 是另一件事：accessory 策略的 app 没有菜单栏、
  从没设过 `NSApp.mainMenu`，这个系统标准快捷键没有任何条目承载，补了本地按键监听。

## [0.7.1] - 2026-07-30

分页交互轮：双端「加载更多」按钮改为滚到底自动加载；修一个设置窗口的 i18n 遗留 bug。

### 变更（双端）

- **分页入口：撤掉底部「加载更多」按钮，改为滚到底自动加载**。起因是用户看 Windows
  主窗口底部那颗按钮，判断它像多余功能——核实结论是**功能不多余，入口形式多余**：
  真实库轻松涨到成百上千条，不翻页只能看最新一批；但按钮常驻占一行（Windows）/
  需要手动点击（mac）都不够顺手。改成滚到接近底部即自动取下一页，按钮撤掉：
  - Windows：判据抽成 `Core/PanelGeometry.ShouldLoadMore` 纯函数（9 条断言，逐闸
    变异反证）；顺带把重排套用方式从「每次 `Clear()` 整表重加」改成保留公共前后缀、
    只动真变的槽位（新增 `Core/ItemDiff.cs`）——自动翻页后单次会话轻松涨到两三千条，
    每来一个新节点都要重排一次，冒烟 421 → 432；
  - macOS：`TimelineView.swift` 里的按钮换成 `LazyVStack` 尾部一个近乎不可见的哨兵
    视图，靠惰性渲染「进入视口才实例化」本身当滚到底判据，`loadMore()` 加重入门；
    用真实二进制 + 一次性合成的 3000 条数据集实机验证，90 步慢滚（约 540 次滚轮
    事件）精确触发 2 次加载（500→1000→1500），未见连锁拉全库；`swift test` 89 → 92；
  - **两端都复现但都未修的存量缺陷**：把滚动条直接拖到最底（或等价的一次性大跳）
    会让正文稳定空白、小幅回滚不自愈，只有大幅滚开才恢复。Windows 端是
    `ItemsRepeater` 对未测量内容的 extent 估算问题；macOS 端 `LazyVStack` 复现了
    几乎相同的症状。均已确认与本轮改动无关（Windows 用 worktree 对照过旧版本一样
    会空白），留作已知问题、不阻塞本次发布；
  - `design/strings.json` 的 `timeline.loadMore` 键保留不删——两端代码都不再引用，
    但它不是存储契约，留着不影响门禁，将来想加回来也不必重新过一次双端同源关。

### 修复（macOS）

- **设置窗口标题的语言硬编码**。`AppDelegate.openSettings()` 里 `window.title` 写死了
  一段中文拼接字符串，四语接线那轮漏改，导致设置窗标题不跟随界面语言切换；改用
  `Strings.f("app.settingsTitle", version)`，并在语言切换通知里补一次刷新（此前该
  通知只重建了状态栏菜单，没重设已经打开的设置窗标题）。

### 内部

- **演示数据集补齐日语/韩语正文**（`scripts/demo_dataset.py`）：此前 Ja/Ko 只能切
  界面 chrome、时间线内容退回英文；现补上 12 条节点 + 6 个代号定义 + 1 条调研摘录
  的自然日语/韩语翻译（非逐字机翻），`scripts/check-demo-dataset.py` 从硬编码
  zh/en 泛化为遍历四语的结构一致性 + 文案两两不同 + 两端等价校验。纯开发/截图
  工具链改动，不影响已发布二进制。

## [0.7.0] - 2026-07-30

多语言轮：界面四语可切，识别词表**四语常开**；面板可折叠到只剩标题栏；文档与截图出英文版。

### 新增（双端）

- **四语界面**：简体中文 · English · 日本語 · 한국어，设置里切换**即时生效**（未保存
  关窗会回滚）。文案有唯一事实源 `design/strings.json`（71 键 × 4 语言），两端各一份
  必须同源的副本，CI 硬校验——建表时实测两端在**只有中文**的情况下就已漂移 8 处以上。
- **识别词表四语常开**：状态关键词与类型分类同时认四种语言，与界面语言无关——中文界面
  照样读得懂日文 agent 回复。**已入库的历史保持原语言不重写**，只有新生成的内容跟随当前
  设置；`kind` / 代号状态落库的仍是中文枚举值，切语言不动一个字节、不需要迁移。
- **折叠到标题栏**：头部 chevron 一点收成只剩标题栏、再点展开回原高度，**顶边不动**
  像卷帘；折叠态锁住竖向尺寸（拖不动），折叠状态与折叠前高度都记住，重启还在。
- **英文文档**：`README.en.md` 与 `windows/README.en.md`，两版互指；README 截图也出了
  英文版——演示数据集扩成中英两套，因为只切界面语言会拍出「英文 chrome + 中文内容」
  的混语图。

### 修复

- **日/韩语境下的状态误判**（双端）。否定词的位置三语不同：中文前置、日语后置
  （完了して**いない**）、韩语两头都有。原先只看「关键词前两字符」，对日韩完全够不着，
  「完了していない」会被记成"完成"。韩语前置否定改按**어절**判而非按字符——真实语料里
  `이미 완료`(11265)、`제안 완료`(3261)、`잘못`(84805) 都含 안·못·미，按字符会把最强的
  肯定句全杀掉。
- **拉丁关键词缺词边界**（双端）：`prefix`/`suffix` 命中 fix、`networking` 命中 working、
  `disclosed` 命中 closed——第一条在开发语料里几乎无处不在。
- **中日同形词误伤**（双端）：`要求`/`判断` 作日语词加入分类表，但简体中文写法相同且是
  高频通用动词，实测把 31 条任务误判成需求。已排除，改用无碰撞的 `要件/仕様`、`決定/選定`。
- **全角与半角形态匹配失效**（双端）：`ＷＩＰ`、半角片假名 `ﾃﾞﾌﾟﾛｲ`、分离浊点会让子串
  匹配整个落空。
- **设置窗的保存/取消被挤出滚动区**（Windows）：设置项已比任何合理窗高都长，确认动作
  却要先滚动才找得到。动作条改为常驻。
- **空时间线毫无提示**（Windows）：首次运行分不清「还没监听到」还是「起崩了」。
- **头部图标按钮命中区偏小**（双端）：mac 20×20/11pt 字形、Windows 20×20，均已放大到
  ~21×26 的固定命中框。
- **窗口高度记忆值异常时会起成一条缝**（Windows）：竖向钳制挂在尺寸变化事件上，而窗口
  还原跑在订阅之前，存量或被手工改过的畸小值不会被任何回路救回来。

### 内部（不影响使用，但值得记一笔）

- CI 增至**六道关**：新增「文案表同源与完整性」（含"代码引用的键必须在表里"——缺键时
  界面会**回显键名**而不报错）与「演示数据集中英不变式」。
- 截图拍摄流程补上**断电级数据保护**：`try/finally` 与 `trap` 都挡不住进程被硬杀，
  一次被中断的拍摄会把演示库留在真实位置，下一轮再把它当基线备份并"忠实还原"——
  校验全绿而真实数据已被覆盖。现有中断标记 + 固定备份 + 一键救援，双端都已加。

## [0.6.0] - 2026-07-29

本轮由**有人值守的实机复测**驱动：此前标 ⚠️「需真实指针、待复测」的交互项第一次被真人
逐项点过，查出一簇长期静默失效的缺陷——根因是同一个 `null`。

### 新增（双端）

- **快淡入、慢淡出**：面板变亮仍是 `opacity.transitionMs`（180ms）+ ease-out，指针一进来
  立刻可读；变暗改走新 token `opacity.transitionOutMs`（500ms）+ **ease-in**，从容化开，
  不再"看到一半就唰地消失"。
  曲线必须随方向换，不能只拉长时长：ease-out 会把约 87% 的变化挤在前段，500ms 下观感
  反而变成"唰一下再慢慢爬"。ease-in 先稳住、后段化开，才是想要的。
  双端同一套语义（win `OpacityAnimator`、mac `FloatingPanel.updateTrackingAndOpacity`）。

### 修复（Windows）

- **`ItemsRepeater` 不给条目设 `DataContext`——一个 `null` 打掉了整簇交互**（本轮根因）。
  `ItemsRepeater` 不像 `ListView`/`ItemsControl` 会用 `ContentPresenter` 包一层，realize
  出来的元素 `DataContext` 始终为 `null`。模板里的 `x:Bind` 编译成直接绑定、不走
  DataContext，所以**画面完全正常**——但每个 `DataContext is NodeViewModel vm` 的代码后
  处理器都拿到 null 并静默 return。受害面：整条点击展开、chevron 展开、hover 高亮与复制
  按钮、条目右键菜单。代号 chip 不受影响，因为它在 `ItemsControl` 里——这个反差正是佐证。
  修法：`NodeRepeater.ElementPrepared` 里补上 DataContext，所有处理器一起复活。
  定位靠日志探针：`CHEVRON sender=Button dc=<null>`；修复后用 UIA 调用 chevron 自验，
  画面差异从 0.91%（没反应）变为 34.65%（真的展开了）。
- **头部拖动：按住拖不走，点一下松开窗口反而黏着鼠标跑**。原实现借系统原生移动循环
  （`ReleaseCapture` + `WM_NCLBUTTONDOWN`/`HTCAPTION`），这在 WinUI 3 下不可靠——指针输入
  走 XAML island 的 input site 而非顶层 HWND，模态循环常在按键已松开之后才启动，于是它在
  等一个早就发生过的 `WM_LBUTTONUP`。改为手动拖拽：捕获指针 + 按**屏幕坐标位移**调
  `AppWindow.Move`，松开或捕获丢失即结束（后者不处理会一直粘在拖动态）。位移取屏幕坐标
  而非元素内坐标，跨不同 DPI 的显示器才不漂；拖完即存位置。
- **整条点击展开在绝大部分面积上失效**（实机值守发现）。`Tapped` 原先挂在一层夹在中间的
  透明命中层上，而命令/派生纸面块是**不透明 Border、可命中且自身没有 Tapped 处理器**——
  点在它们身上时事件只会往**上**冒泡到条目 root（root 当时也没有 Tapped），那层兄弟命中层
  永远轮不到。可点区域只剩元信息行与块间窄缝。改为把 `Tapped` 挂到条目 root（对齐 mac
  `NodeViews.swift` 的 `.contentShape(Rectangle()).onTapGesture`）并删掉命中层。
- **要点摘要行划不动**：折叠态下派生区最显眼的那行，是全条唯一漏了
  `IsTextSelectionEnabled` 的文本。补上。
- **agent 回复区域右键出不来菜单**：`IsTextSelectionEnabled="True"` 的 TextBlock 会
  **吞掉右键手势**，使其到不了挂在条目 root 的 `Entry_RightTapped`。命令区因为有 ❯ 列、
  Border 内边距、右侧留白等大片非文本像素，右键落在那儿仍能冒泡上去——所以症状表现为
  「只有自己发出的命令范围内有菜单」。修法：给条目内各文本加**元素级** `RightTapped`。
  三处均经有人值守实机确认修复。
  ⚠ 注：这三处与上面的 DataContext 是**两层问题叠在一起**——命中层/属性/事件的修法本身
  必要，但只修它们时仍然全无反应，直到 DataContext 补上才真正生效。

- **单实例保护**：补上 Windows 侧缺失的入口闸——这是一处双端分叉，mac
  `App/main.swift` 一直有（发现有同 bundle id 的进程就 `exit(0)`），Windows 侧从无对应物。
  面板 `IsShownInSwitchers=false`、无任务栏按钮、收进托盘后完全隐身，托盘图标在 Win11
  默认还在溢出区——用户想确认它在不在跑，最自然的动作就是再双击一次 exe，所以这不是
  边缘情况而是常规误操作路径。
  两个实例并存的实际代价：摘要引擎各自挑同一批 `summary_pending` 节点跑 CLI（配额与
  耗时双倍、`summary_attempts` 提前耗尽）；「replay 与 watcher 不并发写 codenames 表」
  与 `AppSettings.Save` 的锁都只是**进程内**保证，跨进程失效；两个托盘图标导致「退出」
  只关掉一个。（SQLite 本身自愈：WAL + busy 重试 + `UNIQUE(agent, session_id, ts,
  command_hash)` 去重 + `file_offsets` UPSERT，故 mac 注释里那句「silently lose writes」
  在 Windows 这套姿态下并不完全成立，已如实记在代码注释。）
  实现：`windows/AgentTimeline/Program.cs` 取代 XAML 生成的 Main（csproj 定义
  `DISABLE_XAML_GENERATED_MAIN`），Main 除首行外与生成版逐字一致，与 mac 同一位置——
  在任何应用对象存在之前退出。闸的粒度是**一个数据库**（名字取 `AppPaths.DatabaseFile`
  的哈希）：不同用户各有自己的 `%LOCALAPPDATA%` 故互不阻塞，同一用户的两个会话
  （RDP + 控制台）共用一份 store 故被拦下——固定名的 `Global\` 会误伤前者，`Local\`
  拦不住后者。实机验证：第二个进程 83ms 退出（ExitCode 0，赶在 XAML/托盘初始化之前）、
  托盘只剩一个图标、强杀后可重启无残留 mutex 死锁、连开三次只剩一个。

### 验证（Windows）

- **provider 档全链路打通**（此前长期挂在「已知未验证事项」里，只验过失败降级链路）。
  新增 `windows/scripts/provider-check/`：本机 OpenAI 兼容 mock（真 HTTP、真协议）+ 端到端
  编排。五项判定全绿——baseUrl 不带 `/v1` 时自动补全、Bearer 头带上、`temperature=0`、
  解析 `choices[0].message.content` → `SummaryJson.Parse`、落库 `summary_source='Provider'`
  且标题被 LLM 值替换。数据安全同 §3b：备份 → 文件级交换 → try/finally 还原 → 计数 + md5
  双重核验。mock **不记录 prompt 正文**（只留长度与 SHA256），Authorization 头日志脱敏。
  仍未验：某个具体厂商端点的响应怪癖（需真凭据，不经手脚本）。

## [0.5.1] - 2026-07-29

### 修复（双端）

- **结果行「引子续接」：首段是过渡句时正文不再整段丢失**。用户上报
  `解释下 TH-0025 是什么任务` 的结果行只有 `TH-0025 是一条安全类 issue,核心是一句话:`。
  根因不是数据丢失，是摘录规则——`ResultExcerpt` 取「首个非空段落」，而 agent 回复
  极常见的形态是「引子 + 空行 + 正文」，首段只是一句以冒号收尾的过渡。把真实回复
  喂给真实实现复现，产出与库中值逐字一致，判定成立（详见 `docs/TEXT-NORMALIZATION.md §3.3b`）。
  - 规则：首段以 `:` / `：` 收尾判为引子，续接下一段直到吃到非引子段，段间单空格
    拼接，仍受 `Clip(500)` 约束；
  - 硬性约束（双端各自断言）：**首段一字不动**，只对被续接进来的段落剥行首
    `> ` / `- `，非引子回复产出与修改前**逐字节一致**；段数上限 4 且累计长度到顶即停；
    末级兜底（未规整原文）不续接，避免把表格行拼进来；
  - **差分执行（本机 2460 条真实 agent 回复）**：变化 350 条（14.2%）、变短 **0 条**、
    旧值全部是新值的前缀（只续接不改写）、冒号结尾 351 → 2（余下 2 条正文全在代码
    围栏里，Excerpt 档按规范丢弃）、均长 90 → 114、空串 0 → 0（§3.4-1 不变式保持）。
  - **不回填**已入库的结果行（本机 357 条中 14 条属该形态），规则只对新节点生效。

### 变更（内部）

- 双端 `TextNormalizer` 提取 `StripLeadingMarkers`（行首 `> ` / `- ` / `1. ` 剥离），
  规整管线与 `ResultExcerpt` 的引子续接共用同一判据。**纯提取重构，规整器语义不变**，
  `docs/normalize-cases.tsv` golden 用例未改动。

## [0.5.0] - 2026-07-28

### 新增（双端）

- **接入第五家 agent：Grok Build**（`~/.grok/sessions/<URL 编码的 cwd>/<uuid>/updates.jsonl`）。
  会话流是 ACP（Agent Client Protocol）通知，与既有四家都不同的三处，按本机
  **87 个真实 session / 27724 行**实证定规则并写进 `docs/SESSION-FORMATS.md §3`：
  - `timestamp` 是 **unix 整秒**（非 ISO8601），两端时间戳解析各走数值分支；
  - **文件内无任何 cwd 字段**，项目名只能由目录名百分号解码后取末段
    （`F%3A%5C…%5Chawk-watcher` → `hawk-watcher`；mac 的 `%2FUsers%2F…` 同理），
    两端都先把 `\` 归一成 `/` 再取末段，保证同一份语料解出同一个项目名；
  - 结果行取 `turn_completed` 之前**最后一条** `agent_message_chunk`——一轮内有多条
    （实测 532 条对 57 个轮次），前面的都是工具调用之间的进度旁白；`task_completed`
    是子任务完成，**不是**轮次完成，不可当结果行。
  - `CanHandle` 锚定到 `updates.jsonl`：同一棵会话树下并存 6 种 `.jsonl`
    （chat_history 91 / events 91 / updates 87 / rewind_points 81 / hunk_records 4 /
    prompt_history 3），宽松匹配会把同一轮对话重复摄取（Kimi A1 同类教训）。
  - 实机验证：357 个 `.jsonl` 中精确命中 87 个、其余 5 种零误匹配；88 条命令 /
    57 条结果行，时间戳零越界、结果行零空串；本机时间线已点亮 12 个 grok 节点。
- **设置页 agent 顺序与命名统一**为 Claude Code / Codex / Grok Build / Kimi Code / ZCode
  （`AgentKind` 声明顺序即展示顺序，mac 侧由 `allCases` 直接驱动）。`zcode` → `ZCode`
  大小写对齐产品名；**落库用的稳定键不变**（仍是 `zcode`），历史数据不受影响。
  mac 侧新增 `AgentKind.settingsLabel`，设置页标签统一取它，避免两端各写字面量而漂移。
- Grok 徽标色 `#64748B`（design tokens 三份同源副本已同步）：xAI 品牌本身是单色，
  四个饱和色里插一个中性石板色在 7px 徽标尺度最易区分，白字对比度 4.76 落在现有
  3.1–4.7 同一档。

### 变更（内部）

- Windows 侧注入块前缀清单从 `ClaudeParser` 私有字段提升为
  `ParserUtil.IgnoredPrefixes` / `IsIgnoredContent`（与 mac `ParserSupport` 同源），
  各 agent 解析器共用一份，避免两端 L1 过滤集各自漂移。Claude 行为完全中性。

### 已知未决

- **Grok 的编排器派发任务书当前不做过滤**：本机 92 条用户消息中 85 条是子 agent
  任务书，只有 3 条真人手打，且**无协议级判据**可与真人会话区分（结构逐字段相同）；
  其骨架是用户自有插件的私有约定，硬编码即过拟合。代价与三个可选项见
  `docs/TEXT-NORMALIZATION.md §4.2c`，需用户拍板。

### 修复（双端解析一致性）

> 起因：四路解析器逐行对拍（每家 agent 一路 + 对抗验证 + 真实语料差分执行）与
> Windows 侧跨端合并审计。共确认 17 处分叉/缺陷，全部修复。

- **Kimi 子 agent 结果行串台**（A1，正在污染时间线）：`agents/agent-N/wire.jsonl` 与
  `main` 共用 `session_<uuid>` 目录名即共用 sessionId，而子 agent 的「问」是
  `system_trigger`（已过滤）、「答」是普通 `content.part` → 结果行被挂到主会话的命令
  节点上，代号词典也混入只源自子 agent 的条目。**子 agent 整文件排除**（与 Claude 侧
  `isSidechain` 同语义），并锚定完整路径形状。
- **codex 会话身份不稳定**（B1）：被 resume/fork 的 rollout 在文件中途还会写入**原会话**
  的 `session_meta`，流式路径逐条重设、重启续扫却只读第 0 行 → 两条路径判出不同
  sessionId；它参与节点 id/唯一键 → **Windows 侧**重扫会插出重复行（该端实测 257 组 /
  514 行，判据为同 `source_file`+同 `source_offset`），两端共有的后果是结果行会挂到
  **另一个 rollout 文件**的命令上。
  ⚠️ 更正：发布前曾按「同文件+同正文」在 mac 库里数出 38 组，复核发现那是**误判**——
  mac 表无 `source_offset` 列，改用严格判据（同文件+同正文+**同时间戳**+不同 session）
  实测为 **0 组**：那些行是用户在不同时刻真的重复输入（如「继续」10 次）。mac 端不会
  重扫已消费字节，故 B1 在 mac 只表现为结果行错配，不产生重复行。改为**只应用本文件第一条** meta；
  mac 261 个 rollout（含 55 个多 meta 文件）两路径不一致数 55 → **0**。
- **codex 首行重读截断在 16 KB**：`session_meta` 首行常大于此（本机 260 个 rollout 里
  169 个 >16 KB），读不到换行就整条放弃 → 重启续扫时项目名退化成 `codex`。改分块读到
  首个换行；真实语料 **261/261 恢复真实项目名**（修前 108 个文件退化）。
- **codex 摘要器自摄取回路**（Windows）：摘要引擎解析到 `codex exec` 时，win 把自己发出
  的每条摘要 prompt 当用户命令收进时间线（其 rollout 写在 `~/.codex/sessions` 下，
  路径匹配永远拦不住）。补整文件禁用，流式与重启续扫两条路径共用判定。
- **codex 注入块泄漏**（A2）：`<task>` 是编排器给用户真实任务加的壳 → 去壳保留正文
  （Windows 修前 37 个节点标题字面是 `<task>`）；`<heartbeat>` 等 11 个标签整条跳过。
- **结果行退化成光秃秃的标题**（A3）：先剥前导标题行再取首段，剥后为空回退含标题原文
  （永不写空串）。
- **时间戳容错两端都不对**：mac 解析失败丢整行（丢命令）、win 回退「当前时间」（节点跳
  顶且 ts 参与唯一键 → 重扫出重复行）。改共同规则：形态放宽 → 顺延**本文件最近见到的**
  时间戳（任意行喂养基准）→ 无前值才丢弃。
- Claude 侧：L1 忽略前缀表两端统一（win 补 2 条并改不含 `>` 匹配，此前
  `<user_instructions>` 等会变垃圾节点）、assistant 多段文本改为全拼接、无 `cwd` 行沿用
  上下文项目名、`queued_command` 与 codex `user_message` 补 trim（不 trim 会让同一条命令
  两端连节点 id 都不同）。
- Codex 技能回显 `[$plugin:skill](本机…SKILL.md)` 双端都剥本机路径（跨机无效且泄漏用户名）。

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
