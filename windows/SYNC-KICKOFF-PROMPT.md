# Windows 同步开工 Prompt（代号词典关键字搜索轮 · 2026-08-01）

> ## ✅ 本轮已完成（2026-08-01）
>
> | 你要求确认的事项 | 结论 |
> |---|---|
> | 1 纯判据函数 + 6 场景断言 | ✅ `Core/CodenameSearch.Filter`，冒烟 451 → **463**（12 条断言） |
> | 2 UI 控件选型与理由 | ✅ `AutoSuggestBox`，但**不能设 `QueryIcon`**——见下，这是实测出来的坑 |
> | 3 实机验证（真实词典） | ✅ 475 条真实词典，UIA 真去打字；含 4 条"靠定义/最近提及命中"的真实案例 |
> | 4 六道 CI 关 | ⏳ 本地能跑的都绿（冒烟 463 / 文案表 74 键 × 4 语言副本同源 / 演示数据集），CI 待推 |
>
> ### 匹配语义
>
> 逐字按你第 2 节实现：子串非前缀、匹配 `Name`/`Definition`/`LastContext` 三个字段、
> 大小写不敏感、查询词先 trim 且纯空白返回全部。两点本端特有的处理：
>
> - 大小写用 `StringComparison.OrdinalIgnoreCase` 而不是当前区域——避免土耳其语 I 那类
>   "换个系统语言搜索结果就变"的坑（你那边 `lowercased()` 同样不受界面语言影响）；
> - `CodenameEntry.Definition` 在本端是**可空**的（mac 是非可空 `String`），判定里单独兜住，
>   并为此加了一条断言（definition 为 null 的条目照样按名字命中、不抛）。
>
> ### UI 控件选型：AutoSuggestBox，但踩到一个坑
>
> 选它而不是裸 `TextBox`，就是冲着它自带"有内容才出现的清空 ×"——正是本轮要的。
>
> **但绝不能设 `QueryIcon`。** 我一开始按习惯给了个放大镜图标，结果模板里查询按钮与清空
> 按钮**占同一个位置**，清空 × 再也不出现。这不是读代码读出来的：UIA 实测发现设了 `QueryIcon`
> 之后控件内**一个 Button 都不暴露**，去掉才回来。已在代码里写死警告注释。
>
> ### 其余按你的清单
>
> - 标题计数用过滤后的条数（复用同一个格式串，没加新 key）；
> - 两种空态分开：`dict.empty`（词典本来就空，此时连搜索框都不摆出来）/ `dict.searchEmpty`；
> - 打开即聚焦：挂在搜索框的 `Loaded` 而不是 `flyout.Opened`——控件每次开都是新建的，
>   `Loaded` 必然在它真正进入可视树之后触发，聚焦不会落空；
> - 搜索状态不跨重开保留（`OpenDictionary_Click` 每次全新构建，query 只是局部变量）；
> - `TextChanged` **没有按 `args.Reason` 过滤**：清空按钮触发的变更归哪一类没有硬保证，
>   漏掉就会出现"点了 × 但列表还是过滤态"。这里没有任何程序性改文本的路径，重画不会自激。
>
> ### 实机验证（真实词典 475 条，UIA 驱动，不是描述"应该"）
>
> ```
> ① 打开即聚焦: 输入框 HasKeyboardFocus = True    占位文案 = 'Search codenames or definitions'
> ② 未过滤:     标题 = 'Codenames (475)'，可见行 475 条
> ③ 搜 'C0':    标题 = 'Codenames (11)'，可见行 11 条
>      bc01013e / C0 / C0-E / C2-67 / C0-I / E-C0E-0006-V14 /
>      ADVISORY-REJECTION-20260726 / E-C0-I-PLAN / REFREEZE-20260722 /
>      FREEZE-20260722 / E-C0-I-TYPESCRIPT
> ④ 无匹配:     标题 = 'Codenames (0)'，结果区 = 'No matching codenames'
> ⑤ 清空 ×:     输入框=''，标题 = 'Codenames (475)'，可见行 475 条
> ```
>
> 11 条与直接查库预测的 11 条**逐条相同**。其中 4 条——`C2-67`、`ADVISORY-REJECTION-20260726`、
> `FREEZE-20260722`、`REFREEZE-20260722`——**名字里根本没有 `C0`**，是靠定义/最近提及命中的，
> 这就是你要的"匹配范围真的生效"的证据（对应你那边搜 N1 命中 T1/T2 的案例）。另外
> `bc01013e` 命中同时验了大小写不敏感与子串（`b‑c0‑1013e`）。重开面板恒为未过滤态——
> 前几轮探针结束时输入框里还留着 `zzz…`，下一轮 ② 仍是 475，这就是不持久化的实证。
>
> ### 探针本身坑了三次，记下来免得下次重来
>
> 1. 外层 `AutoSuggestBox` **不支持 ValuePattern**，可写的是它内部的 `Edit` 子元素；
> 2. 每次过滤都会**新建**结果区元素，开局抓到的引用会陈旧、继续返回旧内容——每次读之前
>    必须按 AutomationId 重新解析。第二版探针就是栽在这里，一度读出"过滤后还剩全量"；
> 3. 清空按钮在 **Raw 视图**里（模板标了 `AccessibilityView=Raw`），`FindAll` 走 Control
>    视图怎么找都找不到，得用 `TreeWalker.RawViewWalker`。**差点把它当成"按钮没渲染"**——
>    本仓库 `memory` 里那条"先证明探针可信，再判产品"第四次生效。
>
> 为此给标题/搜索框/结果区加了 `AutomationProperties.AutomationId`
> （`DictionaryTitle` / `DictionarySearchBox` / `DictionaryResults`）：纯代码构建的控件在
> UIA 树里没有锚点，而本工程没有 UI 测试框架，这类面板的验证全靠 UIA 驱动。
>
> ### 如实说明
>
> - **`dict.empty`（词典整体为空）那条分支没有实测**——要求空库，没为它换库。该分支逻辑与
>   本轮之前相同，只多了一个提前 `return`；
> - 没做高亮、没做模糊匹配、没做回车跳转、没加全局快捷键、没升版本号（按你"本轮不做"）；
> - 共享层我一个字节都没动：`design/strings.json` 与 `windows/AgentTimeline/Assets/strings.json`
>   SHA256 **完全相同**（`3D2BD965…`），直接用你加的两个键。
>
> 下面 `---` 之间是本轮的原始任务书，保留备查。

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
