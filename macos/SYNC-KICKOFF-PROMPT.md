# macOS 同步开工 Prompt（拍摄脚本数据安全 + 英文截图 + 双语文档轮 · 2026-07-30）

> ## ✅ 本轮已完成（2026-07-30）
>
> - **A（最高优先）数据安全两个 bug 已修**（`b5836a7`）。你的判断成立——那套备份/还原
>   范式是 mac 侧写的，两条全中。中断标记 `.shoot-in-progress`（动真实文件之前立）+
>   开跑先查 + 三项全对才清 + 固定备份 `.shoot-backup` + `--recover`；`$swapped` 门。
>   反证：种标记重跑精确拒绝、trap 之后备份之前注入失败「未进入交换阶段」，两次真实库
>   md5/nodes 分毫未动；bug 2 的承重性用隔离目录复现（文件被删且拷不回）。
>   动真实文件前先手工把库另存到脚本之外——你那条规矩照做了。
>   顺带钉死演示语言（此前没钉，产出语言取决于拍摄机系统 UI 语言）。
> - **C 英文截图 mac 半边**：`macos/scripts/demo-seed.py` 补 `--lang zh|en`，重构成
>   `STRUCT`/`CONTENT`/`DEFS` 三段与你那份同形状，**英文串是程序化从你文件里搬的、不是
>   手抄**；拍摄脚本加 `--language`，产出 `screenshot-macos-*-en.png`，`README.en.md`
>   的 macOS 三列已改指英文图。
> - **B 双语文档**：你让我核的 `swift test` 项数——**实跑是 81 项，与你数的一致**，
>   两版 README 无需改正。`docs/` 不翻译、不新建 `macos/README.md` 都照办。
>
> ### 缩放之争已由用户当场定案，两端口径都已改
>
> 2026-07-30 用户在 mac 会话中的原话是：**「按照当前系统默认的设置 重拍」**。
> 即：**各端按自己系统的默认设置拍，不再互相强制缩放**。
> mac = Retina 2x → 1718×1352；Windows = 该机 100% → 859×676，
> **dip 几何与宽高比两端相同**，README 三列同宽仍严丝合缝。
>
> `windows/DEBUG-PLAYBOOK.md` §3b 的「渲染缩放 1pt=2px（mac Retina / win 200%）」
> 已改为「按拍摄机系统默认，脚本不改缩放」。你那边**无需再动图**，859×676 就是结论。
>
> 你上一轮提的第二条教训（转述用户答复要标明是转述）我接受，这段也照此写明了场合与原话。
>
> ### 一处仍待你定的共享层改动（我没擅自动）
>
> CI 第六关 `scripts/check-demo-dataset.py` 目前**只校验 `windows/scripts/demo-seed.py`**。
> mac 那份现在也是中英两套，但不在门禁覆盖内——两端英文内容靠「程序化搬运」保证一致，
> 一旦哪边手改就会无声漂移。**建议**把两套内容抽成共享模块（如 `scripts/demo_dataset.py`）
> 由两端 seed 各自 import，门禁同时校验两端。这动的是共享层，按规则先报不做。
>
> 下面 `---` 之间是本轮的原始任务书，保留备查。

> 用法：在 macOS 机器的仓库根目录启动 agent 会话，把下面 `---` 之间的整段粘贴为首条指令。
>
> 上一轮（四语接线追齐：`98f774f` 折叠层 / `282fcc6` 词表 / `ead9370` 接线）已完成并 push，
> 双端词表经 Windows 侧逐条比对**六张表全部一致**（Changed 17 / Completed 19 / Active 27 /
> 后置否定 11 / 白名单 7 / 类型表 111），哨兵串、折叠表、窗口参数也都对上。
> 本文件整体替换为本轮内容，历史见 git log。

---

你在一台 macOS 机器上，当前目录是仓库 agent-timeline 根目录
（远端 github.com/surebeli/agent-timeline，先 `git pull` 到最新 main）。产品是双端桌面
挂件「Agent Timeline」，两端跑在 **CI 六道关**下，最新发布 **v0.6.0**。六关是：
mac swift test / Core 冒烟 / WinUI msbuild / tokens 同源 / 文案表同源 /
**演示数据集中英不变式**（最后一关是 Windows 侧本轮新加的，见任务 C）。

**本轮三件事**：补一个能丢用户真实数据的坑（A，最高优先）、英文版截图的 mac 半边（C）、双语文档收尾（B）。

## 任务 A（最高优先）：拍摄脚本的数据安全 —— 两个 bug，Windows 侧刚踩过

2026-07-30 Windows 侧在重拍 README 一览时**真实丢了一次用户数据**（已完整恢复）。
两个 bug 都不是 Windows 独有的：我读了 `macos/scripts/shots/shoot-readme.sh`，
**同一套备份/还原范式，两条全中**。下面把原委和你这边的对应位置都写清楚。

### bug 1：`trap ... EXIT INT TERM` 挡不住进程被硬杀

现状（`shoot-readme.sh:79`）：`trap restore EXIT INT TERM`。这三个信号覆盖不了 `SIGKILL`
（`pkill -9`、活动监视器强制退出、上层工具直接终止进程）。

失败链条——Windows 侧就是这么丢的：

1. 一轮拍摄在「已写入演示库、尚未还原」时被**外部强杀**，`trap` 根本没机会跑，
   演示库留在真实位置（`~/Library/Application Support/…/store.sqlite`）；
2. 下一轮开跑，第 88 行照常 `sqlite3 "$DB" …` 取基线、把**演示库当成真实基线**备份，
   拍完又忠实地还原回去 —— `✅ 节点/词典计数一致`、`✅ md5 一致` **两条全打勾**，
   而真实数据被水泥封死。

**校验通过、数据没了**是最坏的一类失败，因为它不报错、不留痕，等你发现时上一轮的
临时目录可能已经被系统清掉。

修法（照 Windows 侧 `windows/scripts/shots/shoot-readme.ps1` 的做法，不必逐字照抄形式，
但三件事都要有）：

- **中断标记**：在数据目录里立一个标记文件（Windows 侧是 `.shoot-in-progress`），
  内容写基线 md5 + 基线计数 + 固定备份路径。**必须在动真实文件之前立起来**——
  立晚了，正好在这中间被杀，下一轮照样踩坑；
- **开跑先查标记**：在就说明上一轮没收尾，**拒绝取基线、拒绝备份**，直接报救援路径；
- **还原三项全对上才清标记**：计数、db md5、defaults 都一致才删。对不上就**留着**标记，
  留着好过让下一轮把演示库当基线；
- **固定备份位置**：备份除了留在本轮 `$WORK/backup`，再落一份到数据目录下的固定路径
  （Windows 侧是 `.shoot-backup`）。救援时不必去猜是哪个 `mktemp -d` 目录、也不怕它被清；
- **`--recover` 开关**：一键用固定备份覆盖真实位置并清标记。

### bug 2：`restore()` 在「什么都没备份」时也会删真实文件

这条更急，因为它**每次前置步骤失败都会触发**，不需要被杀。

现状（`shoot-readme.sh:53-65`）：

```sh
restore() {
  ...
  rm -f "$DB" "$DB-wal" "$DB-shm"                          # ← 无条件删
  for f in store.sqlite store.sqlite-wal store.sqlite-shm; do
    if [ -f "$BACKUP/$f" ]; then cp "$BACKUP/$f" "$SUPPORT/$f"; fi   # ← 有备份才拷回
  done
```

`trap restore EXIT` 会在脚本**任何**退出路径上触发——包括备份步骤之前就失败的情况
（`swiftc` 编译 helper 失败、`sqlite3` 不在 PATH、落点校验不通过、参数写错……）。
那时 `$BACKUP` 是空目录，于是：**`rm -f` 删得掉，`cp` 没有源可拷** ——
真实库当场消失。随后第 77 行 `open "$APP"` 还会把应用拉起来，应用重建一个空库并开始
回填（只覆盖 `backfillDays` 那几天），把现场彻底盖掉。

Windows 侧的经过写在这里当教材：我加完 bug 1 的中断标记后**去测它**，标记确实拦住了，
但 `finally` 里的还原逻辑照样跑了这一段 —— **修数据安全的补丁自己又破了一次数据安全**。
第二次才想起来先把库另存再测。

修法：加一个「真的交换过吗」的标志（Windows 侧是 `$swapped`），在**动真实文件之前**
置位；`restore()` 开头判它，没交换过就**一个文件都不要动**，直接返回。

### 顺带：演示配置没钉住语言

`shoot-readme.sh` 的演示 `defaults write` 写了 engineMode / backfillDays / 两档透明度 /
alwaysOnTop，**没写 `language`**。四语接线之后，语言默认「跟随系统」——所以产出的
截图语言取决于**拍摄机的系统 UI 语言**。Windows 侧那台机器是 en-US，一跑就拍出英文图，
而图看着完全正常（这正是最难发现的那种）。请钉死成中文（Windows 侧写的是
`Language='ZhHans'`，你这边对应 `AppSettings.language` 的 rawValue）。

### 任务 A 的验收

- **反证必须做**：种下中断标记后重跑 → 必须精确拒绝，且**真实 db 的 md5 与节点数分毫
  未动**（不是"看起来没事"，要打印前后 md5 对比）；
- **测之前先把真实库另存一份到脚本之外的地方**。这是 Windows 侧用一次真实数据丢失换来的
  规矩：动真实文件的代码，测之前先手工留一份退路，别指望被测代码自己的备份逻辑；
- 修完跑一次完整拍摄，确认 happy path 没被防护挡住，且三态成品与现有 `docs/assets/`
  下的 mac 三张一致（**不要 install**，先目验）。

## 任务 B：双语文档收尾

Windows 侧本轮加了英文文档，**根 README 是双端共享层**，你需要知道并往后保持同步：

- `README.md`（中文） / `README.en.md`（英文），两版顶部互指，章节逐节对应。
  **今后改根 README 必须两版一起改**——已有校验思路：一级章节数相等、本地图片/链接
  目标全部存在、语言切换链接互指；
- `windows/README.md` / `windows/README.en.md` 同理（Windows 单端，你不用管）；
- **`docs/` 下的深度文档按用户决定不翻译**。两版英文 README 末尾都写明了「这些是工程记录
  而非用户文档，需要哪份可以开 issue」。别自行开翻。

**请你核一件事**：我在根 README 里写了 mac 侧 `swift test` 是 **81 项**
（按仓库 `macos/Tests/AgentTimelineTests/*.swift` 里 `func test` 计数得出：ParserTests 41 /
CodenameFourLanguageTests 14 / StringsTests 7 / CompatibilityFoldTests 6 /
NormalizeGoldenTests 6 / UiTextTests 5 / CorpusSmokeTests 2）。你实跑一次
`swift test`，**如果实际数字不是 81，两版 README 一起改正**。我是从 Windows 机器数源码
得出的，没跑过。

另外 mac 侧目前没有独立的 `macos/README.md`——mac 的构建说明在根 README 里。
本轮**不要**新建，避免又多一处要双语同步的文件。

## 任务 C：英文版截图 —— macOS 那半边

用户定了「英文 README 配英文截图」（2026-07-30 会话中选的 A 方案）。Windows 半边已落地，
mac 半边只能你做。

**前置已就绪，不用你从零设计**：

- `docs/DEMO-DATASET.md` 已扩成**中英两套**，新增一节写明约束（**结构两语逐位相同、
  只有文字换语言**——结构一分叉，README 中英两行的版面就对不齐）；
- `windows/scripts/demo-seed.py` 是参考实现：结构（`STRUCT`/`CODES`）与文案
  （`CONTENT`/`DEFS`）分开存放，`--lang zh|en` 选择。英文文案以它的 `CONTENT['en']` 为准，
  **照抄不要另译**——两端文案不同的话，两版 README 的图就不是同一组数据了；
- `scripts/check-demo-dataset.py` 是 CI 第六关，守两条不变式：结构两语一致、文案两语
  逐条不同（漏译一条会精确报出第几条，已做反证）。你改完 mac 的 seed 后它自动开始校验；
- 成品命名：中文不带后缀，英文带 `-en`（`screenshot-macos-timeline-en.png` …）。

要做三件事：

1. `macos/scripts/demo-seed.py` 支持 `--lang zh|en`，英文文案照抄 Windows 侧的
   `CONTENT['en']` / `DEFS['en']`。⚠ `kind` 与代号 `status` 落库的是**中文 rawValue**
   （`需求`/`完成`/…），两套都一样、**绝不翻译**——那是存储契约，界面靠 `UI/UiText.swift`
   映射；翻了会让 kind 过滤与状态机一起失效；
2. `macos/scripts/shots/shoot-readme.sh` 加语言参数：钉死界面语言（**顺带解决任务 A 里
   那条「演示配置没钉 language」**）、把语种传给 seed、英文产物加 `-en` 后缀。
   没有对应数据集的语种（ja/ko）**直接拒绝**，别悄悄拍出混语图；
3. 拍两遍装上：`screenshot-macos-{timeline,projects,dictionary}.png` 与 `-en` 三张。

### 顺带修一个真缺陷：首图 `docs/assets/screenshot-dark.png` 已过期

那是 README 顶部最显眼的一张（mac 拍的，Windows 这边动不了），**双语都过期**：

- 图里的过滤器写着「**阶段**」——这个标签在之前一轮已全仓重命名为「**类型**」。
  也就是说 README 首图展示的是一个产品里已经不存在的标签；
- 时间戳是 07-28，且只有中文一版。

请重拍，并出中英两版（`screenshot-dark.png` / `screenshot-dark-en.png`）。
`macos/scripts/shots/` 里没有拍首图的编排（当时是手工拍的），这次**建议顺手脚本化**——
不然下次改标签又会留下一张过期的门面图。同理还有 `screenshot-macos-settings.png`
（手工资产，中文，README 里以链接形式引用），也需要一版英文。

做完把 `README.en.md` 里那段说明改掉——现在写的是「macOS row and the hero image still
show the Chinese UI ... queued for the macOS side」，你补齐后这句就不成立了。

## 本轮不做

- 追任何新功能。`timeline.unpin` 在 Windows 侧仍无对应控件（mac 有，照常用）；
- `settings.apply` 两端都没有「应用」按钮，键先留着；
- 翻译 `docs/`。

## 执行规则

- 任务 A / B / C 分别独立 commit（中文 commit message，风格参考 `git log`）；
- **改共享层先停下来报告方案**（`design/`、`docs/`、根 `README*.md`）。
  Windows 侧本轮在这条上越线了两次（改了 `design/strings.json` 的 `dict.firstSeen`
  与新增 `docs/TEXT-NORMALIZATION.md` §3.6，都是先改再报），已如实记在
  `windows/SYNC-KICKOFF-PROMPT.md` 的交付对账里。别学。
- `swift test` 全绿 + `python3 scripts/check-strings.py .` +
  `python3 scripts/check-demo-dataset.py .` 都绿了再 push，CI 六道关自动回归。

## 最终交付

1. 任务 A / B / C 落地并 push，CI **六道关**全绿（本轮新增第六关：演示数据集中英不变式）；
2. 任务 A 的反证结果：**贴出种标记后重跑的输出，以及前后 db md5 对比**；
3. `swift test` 的真实项数，以及若与 81 不符时两版 README 的改正；
4. 本文件顶部标记本轮完成，或更新为下一轮内容；
5. 汇报：完成项 / 新发现问题 / 仍不一致的点。**如实报**——对不上就说对不上，不要凑。
   涉及用户决策的结论，**写明是谁在什么场合说的**（上一轮两端在「200% 重拍」上来回推了
   三轮，根因就是转述被当成了决定）。
