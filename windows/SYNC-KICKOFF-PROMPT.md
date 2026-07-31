# Windows 同步开工 Prompt（代号词典关键字搜索轮 · 2026-08-01）

> 用法：在 Windows 机器的仓库根目录启动 agent 会话，把下面 `---` 之间的整段粘贴为首条指令。
>
> 上一轮（开机自启动）已完成，见 git log 里本文件的上一版；随后又有一轮「claude 项目名
> 钉在会话启动目录」，任务书当时开在 `macos/SYNC-KICKOFF-PROMPT.md`（mac 判断要不要
> 对齐），跟你这边无关，你不用回看。v0.7.5 已发布（双端项目名归属修复 + mac 侧一个
> 自产 bug 的热修）。本文件整体替换为本轮内容，历史见 git log。

---

你在一台 Windows 11 机器上，当前目录是仓库 agent-timeline 根目录
（远端 github.com/surebeli/agent-timeline，先 `git pull` 到最新 main）。产品是双端桌面
挂件「Agent Timeline」，两端跑在 CI 六道关下，最新发布 v0.7.5。

**本轮任务：代号词典面板加关键字搜索，追齐 mac 同轮。** 用户直接需求（原话「对于字典
需要增加个需求，就是能关键字搜索字典，比如搜N1，能定位到字典」），不是任务书里来的，
也不是 scope creep——记在这里备查。设计先跟用户过了一轮方案才动的手，用户拿真实词典
（203 条真实数据）简单测过，反馈「符合预期」。

mac 侧我已经做完、实机验证过、push 了（commit `168694a`）；你这边需要独立实现
Windows 的等价 UI，**但匹配语义必须跟 mac 完全一致**——这条不是"供参考"，是本轮的
硬约束，见下面第 2 节。

## 1. mac 侧做了什么

- **入口**：`CodenameDictionaryView.swift` 的标题下面加一个常驻搜索框（不是开关，面板
  本来就是主动点开的）。面板一打开搜索框**自动聚焦**，可以直接打字，不用先点一下；
  输入即时过滤，不需要回车；有内容时显示一个清空按钮。
- **状态生命周期**：搜索词**不跨会话持久化**——面板每次重开都是全新状态、从空开始。
  这是"随手查一下"的入口，不是常驻筛选器，别做成设置项存起来。
- **标题栏计数**：改成显示**当前可见条数**而不是总数（不过滤时两者相等，复用同一个
  格式串，没加新 key）。
- **两种空态要分清**：字典本来没有任何代号（`dict.empty`，已有）与"词典有条目、但搜索
  没匹配到"（新增 `dict.searchEmpty`）是两种不同的空——都显示一样的空文案会让用户以为
  自己的代号丢了。

## 2. 匹配语义——两端必须逐字一致，这是本轮的硬约束

```
match(entry, query) =
    entry.name.toLowerCase().contains(query.toLowerCase())
    || entry.definition.toLowerCase().contains(query.toLowerCase())
    || entry.lastContext.toLowerCase().contains(query.toLowerCase())
```

- **子串匹配，不是前缀匹配**：复合代号（`REQ-AUTH-3`、`T-PLUGIN-00`）用户可能只记得
  中间一段（比如搜 "AUTH"），前缀匹配找不到；
- **匹配范围不只是代号本身**，也匹配 `definition`（定义）和 `lastContext`（最近提及
  摘录）——用户可能只记得内容（"登录相关的那个"）不记得代号叫 N1 还是 N2。mac 侧
  实机验证过这条：搜 "N1" 命中的 3 条里，有 2 条（T1/T2）是靠 `lastContext` 里提到
  "N1" 命中的，不是名字本身匹配；
- **大小写不敏感**；
- **查询词先 trim，纯空白当作没有搜索词**（返回全部，不是返回空）；
- 三个字段任一命中即算命中，不需要全部命中。

mac 侧这条判据抽成了纯函数 `TimelineViewModel.filterCodenames(_:matching:)`（`nonisolated
static`，不用起真实 Store 就能测，见 `macos/Tests/AgentTimelineTests/
TimelineViewModelTests.swift` 的 6 条用例：空查询返回全部、大小写不敏感、子串非前缀、
匹配定义、匹配最近提及、无匹配返回空）。建议 Windows 侧同构：抽一个纯函数 + 对齐这
6 个场景的断言，别把过滤逻辑散落在 UI 事件处理里验证不到。

## 3. 共享层已经改完，你只需要 pull

`design/strings.json` 新增两个键（四语齐全），紧跟在 `dict.title` 后面：

```json
"dict.searchPlaceholder": {
  "zh-Hans": "搜索代号或定义",
  "en": "Search codenames or definitions",
  "ja": "コード名または定義を検索",
  "ko": "코드명 또는 정의 검색"
},
"dict.searchEmpty": {
  "zh-Hans": "没有匹配的代号",
  "en": "No matching codenames",
  "ja": "一致するコード名がありません",
  "ko": "일치하는 코드명이 없습니다"
}
```

`windows/AgentTimeline/Assets/strings.json` 已同步复制（字节一致，`check-strings.py`
本地跑过全绿，74 键 × 4 语言）。UI 文案用这两个键，别再手写一遍。

## 4. Windows 侧要做的：UI 控件自己选，语义不能变

Windows 的词典面板是 `MainWindow.xaml.cs` 里 `OpenDictionary_Click` 纯代码构建的
`Flyout`（`StackPanel` + 标题 `TextBlock` + 可滚动的 `DictionaryRow` 列表），跟 mac 现在
这版结构基本对应。搜索控件建议用 WinUI 现成的 `AutoSuggestBox`（比纯 `TextBox` 更贴合
"输入即过滤"这个场景，自带清空按钮），但不强求——用你们觉得最贴合 WinUI 习惯的控件都
行，**UI 控件选型是你的判断，匹配语义不是**。

具体要做的：

1. 抽一个纯判据函数（同构 mac 的 `filterCodenames`，签名用你们自己的类型），覆盖第 2
   节列的语义 + 6 个测试场景，`CoreSmokeTest` 里断言；
2. Flyout 里加搜索框，`OpenDictionary_Click` 里过滤 `App.Registry.All()` 的结果再传给
   列表构建；
3. 标题计数用过滤后的条数；
4. 区分两种空态（`dict.empty` vs `dict.searchEmpty`）；
5. 搜索框打开即聚焦（`AutoSuggestBox`/`TextBox` 有 `Focus()`，Flyout 打开时机调用，
   参照你们已有的类似聚焦处理）；
6. 搜索状态不跨 Flyout 重开持久化——`OpenDictionary_Click` 每次都是全新构建，天然满足，
   注意别不小心把 query 存到了字段里跨调用复用。

## 需要你确认并回报的事项

1. 纯判据函数 + 断言，逐项对齐上面 6 个测试场景，`CoreSmokeTest` 冒烟数变化；
2. UI 控件选型与理由（`AutoSuggestBox` 还是别的，为什么）；
3. 实机验证：**用真实词典数据**（不是随手编两条），截图或描述搜索前后的过滤效果，
   包含至少一个"靠 definition/lastContext 命中而非 name 本身"的真实案例（mac 侧就是
   这么验证的，能证明匹配范围真的生效了，不是只测了最简单的名字匹配）；
   自动聚焦、清空按钮、两种空态分别截图/描述确认；
4. Windows 侧现有 6 道 CI 关是否受影响（这轮改了 `.cs`/`.xaml`，msbuild/dotnet smoke
   会跑到；`design/strings.json` 只是新增键，Strings sync 关应该没问题但请实跑确认）。

## 本轮不做

- 不做高亮匹配片段（比如把命中的子串加粗）——mac 侧也没做，先看过滤能力本身够不够用；
- 不做模糊/拼写纠错匹配，纯子串够用，别做过度设计；
- 不做回车跳转到第一条匹配——过滤完还是点那一行进入既有的"跳到定义节点"逻辑；
- 不在词典入口之外加全局搜索快捷键；
- 不升版本号、不打 tag、不发 release——这轮纯功能开发，发布时机由用户定。
