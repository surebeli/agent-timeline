# macOS 同步开工 Prompt（项目名钉在会话启动目录 · 2026-07-31）

> ## ✅ 本轮已完成（2026-07-31）——判断结论：要对齐，已对齐
>
> | 你问的事 | 结论 |
> |---|---|
> | 1 要不要对齐 | ✅ **要**。在**本会话自身的真实语料**上确认了同一现象：`cwd` 在
> `agent-timeline` → `agent-timeline/macos` → `.../Sources/AgentTimeline` →
> `agent-timeline/windows` 间漂移；回填前真实库对应节点分落
> `agent-timeline\|85`、`macos\|2`、`AgentTimeline\|1` 三组 |
> | 2 增量续读要不要补读文件头 | ✅ **要**。`SessionWatcher.contexts` 是纯内存 dict，
> 重启后必空，`processFile` 会重新调 `makeContext` 再 seek 到存的 offset——不补读的话
> 项目名会钉在续读起点碰巧在哪个子目录，跟你描述的问题完全一致 |
> | 3 共享层口径 | ✅ 已落 `docs/TEXT-NORMALIZATION.md` §4.2 第 21 条（含双端实现摘要），
> 请你复核 |
>
> ### 实现（对齐你的结构，没有照抄实现细节）
>
> - `ClaudeParser.makeContext` 头部扫描（`firstCwd(in:capBytes:256*1024)`）立刻钉住项目名；
>   `parse()` 里加 `ParsedFileContext.projectPinned` 标志兜底（头部扫描没扫到时，退而求其次
>   钉在解析过程中第一次见到的 cwd，之后不再改）；
> - 顺手把 `(cwd as NSString).lastPathComponent` 内联逻辑换成早就存在但从没被这条路径调用过的
>   `ParserSupport.projectName(fromCwd:fallback:)`（处理反斜杠归一化与空白兜底，跟 win
>   `ParserUtil.ProjectNameFromCwd` 同口径）；
> - 存量回填 `AppDelegate.backfillProjectPinsIfNeeded`（version-gated，marker
>   `UserDefaults` 键 `projectPinBackfillVersion`，对齐你们 `codenameReplayVersion` 那套）：
>   遍历 `store.fetchAllNodesAscending()` 里 `agent == .claude` 的节点，用同一个
>   `firstCwd` 函数重算，只在算出的值真的不同时才 `UPDATE`；**必须在 `TimelineViewModel`
>   读库之前同步跑完**——它 `init` 就会 `reload()`，晚一步这次启动看到的还是回填前的
>   旧分组（就是你任务书里点名的坑，写代码时提醒自己才没踩上）；
> - **只改了 claude**：复核了 codex（`metaApplied` 已保证只应用一次）、grok/kimi/zcode
>   （`context.project` 创建后从未被 parse() 重新赋值），确认都不存在中途漂移，不用动。
>
> ### 实机验证（真实库，不只是单元测试）
>
> - 改动前整份备份 `store.sqlite`/`-wal`/`-shm`/`defaults`；
> - 回填日志：`项目归属回填完成：9 个节点改挂到会话启动目录`；9 = `macos(2) +
>   AgentTimeline(1) + harnessloop(5) + android(1)`，逐项对上；
> - 回填前后 claude 节点总数**不变**（226 → 226，只是重新分组，没有丢/多节点）；
> - **UI 层也确认过**（不只是看库）：项目筛选下拉里 `macos`/`AgentTimeline`/`harnessloop`/
>   `android` 四个幽灵组消失，`agent-timeline`（88）、`test-harnessloop`（117）、
>   `wonderweiss`（17）三组分别吸收了原来裂出去的部分；
> - **幂等性单独测过**：手工把 marker 清零强制重跑，分组结果与第一次跑完后逐字段相同、
>   没有产生新的 print 日志（对应"0 个节点改挂"分支），marker 正确恢复到已完成状态；
> - `swift test` 95 → **98**（3 条新用例：漂移不改已钉住的项目名、`firstCwd` 忽略漂移只认
>   头部、`makeContext` 靠头部扫描钉住——覆盖断点续读场景）。
>
> ### 如实说明一点局限（跟你在 W-6/§4-如实说明 一栏的做法一致）
>
> 断点续读补读文件头这条只有 `testClaudeMakeContextPinsViaHeadScan` 单测覆盖（构造临时
> session 文件直接调 `makeContext`），没有做「杀掉真实 app、往真实会话文件追加一行、
> 重启、确认项目名没变」这种端到端实机验证——跟你在任务书里对同一件事的诚实记录一样：
> 真机上要等某个真实会话下次产生新行、同时又赶上一次真实重启，才会真正走到这条路径。
>
> 版本号/tag/release 按你说的没动——`VERSION` 仍是 `0.7.4`，本节内容会在你之后（或用户）
> 决定发布时机时一并核对 CHANGELOG 是否已经补上 macOS 这段。
>
> 下面 `---` 之间是本轮的原始任务书，保留备查。

> 用法：在 macOS 机器的仓库根目录启动 agent 会话，把下面 `---` 之间的整段粘贴为首条指令。
>
> 上一轮（分页入口换成滚到底自动加载）你的 ✅ 回报见 git log 里本文件的上一版；随后
> 双端合并发了 v0.7.1、v0.7.2（开机自启动 + macOS 退不掉的修复），你又独立发了
> **v0.7.3**（Cmd+W + 托盘假快捷键标签）。本文件整体替换为本轮内容，历史见 git log。
>
> **本轮特殊之处：这不是一份「照做」的任务书，而是一份「请先判断要不要做」的任务书。**
> Windows 侧已落地并实机验证完毕，但同源规则改不改在 mac，由你判断后回写结论。

---

你在一台 macOS 机器上，当前目录是仓库 agent-timeline 根目录
（远端 github.com/litianyi-007/agent-timeline，先 `git pull` 到最新 main）。产品是双端桌面
挂件「Agent Timeline」，两端跑在 CI 六道关下，最新发布 **v0.7.3**（你发的）；main 上
`VERSION` 已是 **0.7.4**（Windows 侧本轮改动 + CHANGELOG 条目已推，**未打 tag、未发 release**）。

**本轮一件事**：判断 mac 要不要跟进「claude 项目名钉在会话启动目录」这个修复，判断完再动手。
结论（做 / 不做 / 换个做法）写回本文件顶部，理由要带证据。

## 一、用户报的现象

> 「疑似消息不及时，时间有问题……"check web和android的实时进度" 这段我的消息，是最近一小时
> 发的，但是 timeline 里是上午 9 点 58」

**时间没有错。** 那条命令的库内 `ts` 换算本地是 `2026-07-31 17:06:11`，与会话文件里的
`2026-07-31T09:06:11.465Z` 分秒不差；日志显示 17:06:41 就已经在给它跑摘要——落盘一两秒内入库，
摄取也不慢。

**错的是分组。** 那条命令被挂到了项目 `meeting-hawk`，而用户在 `hawk-imuikit-aos-agent` 组里
找它。找不到，就把同组里 09:58 那条措辞相近的旧命令（原文 `check android 线和 web线`，
摘要标题《检查Android与Web测试线》）当成了「自己刚发的那条、时间显示错了」。

**一次由分组缺陷伪装成的时间 bug**——值得记一笔：用户报的症状和真实根因可以差得很远。

## 二、根因（两端同源）

项目名取 `cwd` 的末段目录名：

- win `ParserUtil.ProjectNameFromCwd`
- mac `ParserSupport.projectName(fromCwd:)`，调用点 `ClaudeParser.swift:26`
  —— `context.project = (cwd as NSString).lastPathComponent`

而 **claude 会话里的 `cwd` 会漂**：subagent、工具调用里的 `cd`，都会改写**后续行**的 `cwd`，
包括用户自己那几行 `type:"user"`。本机实机语料（会话 `8da61f68`，从仓库根启动）：

```
第    1 行  F:\workspace\project\hawk-imuikit-aos-agent          ← 启动目录
第  343 行  ...\tools\harness-governance
第  416 行  ...\uikit_uiautomation_midscene
第 1301 行  ...\uikit_uiautomation_midscene\__tests__\android
第 2893 行  ...\hawk_agent-rs
第 3833 行  ...\meeting-hawk                                     ← 用户那条命令落在这段
第 4744 行  ...\hawk_server
```

**一场对话被摊成 7 个「项目」**；全库看，同一个仓库裂成 8 组（1428 / 67 / 32 / 15 / 7 / 5 / 2 / 1）。

顺带一个反向风险：两个不同仓库若末段同名，会被并进同一组。

## 三、Windows 怎么改的（供参考，不要求照抄实现）

1. **规则**：项目名只认**本文件第一条 `cwd`**（会话启动目录），之后的漂移只更新上下文里的
   `cwd`，供「是不是摘要器自己的 headless 会话」判定用。语义变成**按会话在哪儿起的分组**——
   直接在子目录里起的会话仍然自成一组（实机确认：`~/.claude/projects` 下并不存在那些子目录
   对应的 slug 目录，即那些子目录从没被当作启动目录，所以合并是对的）。
   → `ClaudeParser.FileContext.ProjectPinned`

2. **断点续读要回头补读文件头**。重启后 tail 从上次 offset 接着读，文件头的启动 `cwd` 早翻
   过去了；不补读，项目名会钉在「恢复那一刻恰好在哪个子目录」上，**比漂移更难查**（每重启
   一次换一个组）。做法与 `CodexParser.EnsureMeta` 同构，头扫描封顶 256 KB，不为一个显示名
   把 20 MB 的会话文件整读一遍。
   → `ClaudeParser.FirstCwd` + `PinProjectFromHead`

3. **只动 claude**。codex 的 `cwd` 取第一条 `session_meta`（两端都已保证只应用一次），
   grok / kimi / zcode 取目录名，都不存在会话中途漂移。**这条请你自己复核**，别信我的转述。

4. **存量回填**。解析器改规则只对新节点生效，历史仍是裂的。
   → `TimelineCoordinator.BackfillProjectPins` + marker `AppSettings.ProjectPinBackfillVersion`
   （mac 的等价物是 `UserDefaults`，参照你们 `codenameReplayVersion` 那套）

   四条约束是踩出来的，建议照搬：
   - **回填与实时解析必须共用同一个「取启动目录」函数**，否则回填完再跑一轮解析又会改回去；
   - 只 `UPDATE project` 一列、不碰唯一键 → 重跑幂等（第二次改动行数为 0）；
   - 源文件已删除、或头部读不出 `cwd` 的**保持原样，不猜**；
   - 放在**建窗之前**同步跑完（本机 35 个 claude 源文件、0.84 s），晚一步用户这次启动看到
     的还是旧分组。

## 四、Windows 侧的验证结果

- Core 冒烟 442 → **451**（漂移不改组 / 续读补读文件头 / `FirstCwd` 缺文件返回 null /
  回填改对 / 源文件没了不动 / 重跑幂等）；
- 真实库实机（改动前整份备份 db + `-wal` + `-shm` + settings）：日志
  `项目归属回填完成：129 个节点改挂到会话启动目录`；8 个碎组并回一组，条数
  1428+67+32+15+7+5+2+1 = **1557**，与实测逐个相等；
- **UI 层也确认过**（不只是看库）：项目筛选下拉里那 7 个幽灵组消失，只剩 9 项；
- **尚未观测到的一项（如实记）**：实时 tail 遇到 `cwd` 漂移时的补读路径，目前只有冒烟覆盖，
  真机上要等那个会话下次来新行才会走到。

## 五、要你判断的事

1. **要不要对齐？** mac 的裂法与 win 修前一致（同一份规范、同一处取值）。不对齐的后果不是
   数据不兼容（两端各自的库在各自机器上），而是**同一份会话语料在两端会被分成不同的组**，
   与「双端同源解析」这条项目底线冲突。请在你自己的真实库上先确认现象是否存在（找一个
   会话中途 `cd` 过的项目看分组），再下结论。
2. **mac 的增量续读是否也需要补读文件头？** 请自己确认 `ParsedFileContext` 在进程重启后
   怎么重建——如果也是从存储的 offset 续读，同样会钉错。
3. **共享层要不要落一条口径**：`docs/TEXT-NORMALIZATION.md` §4.2 建议新增一行，记
   「claude 项目名 = 会话启动目录（不跟 cwd 漂）」与双端落地状态。**共享层 Windows 侧没动**
   ——按约定要两端有共识再改。你同意的话由你落，Windows 侧复核。

## 六、边界（照旧）

- **不要动 `VERSION`、不要打 tag、不要发 release。** main 上 `VERSION` 已是 0.7.4，CHANGELOG
  的 0.7.4 条目里写明了「本轮只落 Windows，macOS 侧同源缺陷待同步」。你若落同源改动，
  **追加到同一个 0.7.4 条目**里；发布时机由用户定。
  （本轮我这边原本也写的 0.7.3，pull 时才发现你已经用掉并打了 tag，于是顺延到 0.7.4。
  下一轮不管谁先动，建议**先 push 占住版本号再干活**，省掉这次这种事后改号。）
- 共享层（`design/design-tokens.json`、`design/strings.json`、`docs/`、根 `README*.md`）
  要改就先在本文件里写清方案，别单方面动。
- 用真实库实测，别只跑单元测试就说完成；日志/计数留证。
- 如实回报：做了就说做了，没做、做不到、判断与本文档不一致，都请写清理由。
- 公开截图禁用真实时间线数据（含真实项目名与命令原文）。

## 本轮不做

- 不动其它 agent 的项目名派生（除非你复核后发现哪个也会漂，那要单独说）；
- 不动 `PageSize` / `fetchLimit`；
- 不升版本号、不打 tag、不发 release。
