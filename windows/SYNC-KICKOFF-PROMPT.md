# Windows 同步开工 Prompt（演示数据集日韩语料轮 · 2026-07-30）

> ## ✅ 本轮已完成（2026-07-30）
>
> 本轮是**核实**，逐条给证据：
>
> | 核实项 | 结论 |
> |---|---|
> | 在 Windows 机器上跑 `check-demo-dataset.py` | ✅ `12 节点 × 4 语种 × 2 端` 全绿——你在 mac 上跑的结论在 Windows 上成立 |
> | `windows/scripts/demo-seed.py --lang ja` / `ko` 实跑 | ✅ 四语各灌一次，均 12 节点 / 6 代号；无路径分隔符或编码意外；`kind` 仍是中文 rawValue（存储契约未被翻译） |
> | `shoot-readme.ps1 -Language Ja` 实跑 | ✅ 脚本无语法错、`$seedLang=ja` / `$suffix=-ja`，产出 `screenshot-windows-*-ja.png`，还原三项全绿 |
> | 产出是否真日文而非混语 | ✅ 目验：chrome（`すべて`/`種別`/`今日 · 7 件`/`タスク`·`修正`·`決定`）与条目内容（`昨夜のフル実行ジョブのOOMを修正してほしい`）都是日文 |
> | `-Language Ko` | ✅ 一并跑通，产出 `-ko` 三张 |
>
> **你指出的死代码已删**：`windows/scripts/demo-seed.py` 里那份本地 `CODES` 是抽共享模块时
> 漏删的。它确实无害，但**看起来像权威定义**——谁去改那份局部表都会以为改了行为，实际
> 循环读的一直是 `DATA.CODES`。这类"看着生效其实没接线"的残留比缺代码更难查，谢谢揪出来。
>
> **给你删掉的兜底分支补回来了**（一处判断，说明理由）：`$seedLang` 那个 switch 现在
> 四档齐全、`default` 分支不可达（`ValidateSet` 先拦），但我把 `throw` 留下了——
> `switch` 无匹配又无 `default` 时返回 `$null`，会一路传成 `--lang $null`，那是
> "看着跑完了、其实灌错语种"的静默失败。将来 `AppLanguage` 加第五档而忘了补演示正文，
> 就会撞到这条而不是悄悄产出混语图。
>
> **日韩两套截图本轮未 install**（README 不新增日韩行，按你的说明它们是社媒素材）。
> 需要时 `-Language Ja` / `-Language Ko` 各跑一轮即可，产出在临时目录。
>
> 下面 `---` 之间是本轮的原始任务书，保留备查。

> 用法：在 Windows 机器的仓库根目录启动 agent 会话，把下面 `---` 之间的整段粘贴为首条指令。
>
> 上一轮（折叠功能移植轮）已全部完成并对账，见 `c5e9e6c` 及本文件此前版本（git log 可查）。
> 本文件整体替换为本轮内容。

---

你在一台 Windows 11 机器上，当前目录是仓库 agent-timeline 根目录
（远端 github.com/surebeli/agent-timeline，先 `git pull` 到最新 main）。产品是双端桌面
挂件「Agent Timeline」，两端跑在 CI 六道关下。

**本轮背景：给共享演示数据集扩了一套真实的日语/韩语正文，用于海外（X）社媒素材截图。**
这是**共享层改动，已由 mac 侧做完并本地验证过**；本轮 Windows 需要做的是**核实**，
不是重新实现。改动分两类，请分别对待。

## 改了什么

### 1. 共享层（`scripts/`、`docs/`）—— mac 侧已改完，你只需要 pull + 核实

- **`scripts/demo_dataset.py`**：`LANGUAGES` 从 `("zh", "en")` 扩到
  `("zh", "en", "ja", "ko")`；`CONTENT`/`DEFS`/`RESEARCH_CTX` 三个字典各加了
  `'ja'`/`'ko'` 两个键，内容是 12 条时间线节点 + 6 个代号定义 + 1 条调研摘录的自然日语/
  韩语翻译（不是逐字机翻）。**结构**（`NODES`/`CODES`）完全没动——四语共用同一套 agent/
  项目/session/时间戳/kind/代号生命周期，只有文案换了语言。
- **`scripts/check-demo-dataset.py`**（CI 第六关）：原来硬编码只校验 zh/en 两语，现在
  泛化成遍历 `LANGS = ("zh","en","ja","ko")`：结构四语一致、文案两两不同（穷举语言对）、
  两端产出等价这三条不变式对四语全量生效。**我已本地跑过
  `python3 scripts/check-demo-dataset.py .`，四语 × 两端全绿**（含 windows 端 seed，
  纯 Python + sqlite3，在 mac 上也能跑，逻辑与 Windows 机器上跑应该一致，但请你在
  Windows 机器上也跑一遍确认——这条我没法在你的机器上验证）。
- **`docs/DEMO-DATASET.md`**：更新语种说明、`--lang` 用法示例、成品命名规则
  （`-ja`/`-ko` 后缀），并注明日/韩两套不进 README、只用于社媒素材。

### 2. `windows/` 目录下两处机械改动 —— mac 侧代做，请你核实

我为了让 CI 第六关能跑通（它会 subprocess 调用 `windows/scripts/demo-seed.py`），
和为了让 Windows 侧脚本别落后于共享数据集，动了以下两个**你的文件**，请仔细核实：

1. **`windows/scripts/demo-seed.py`**：`--lang` 校验从 `('zh','en')` 扩到
   `('zh','en','ja','ko')`，只改了这一行校验加对应的用法注释，逻辑没动。这是纯 Python
   文件，我在 mac 上跑通过了（`check-demo-dataset.py` 会 subprocess 调用它），但**我从没
   在真实 Windows 环境跑过这个文件**，请你 `python demo-seed.py <db> --lang ja`（和
   `ko`）实跑一次确认没有平台相关的意外（路径分隔符、编码等）。
2. **`windows/scripts/shots/shoot-readme.ps1`**：`$seedLang` 那个 switch 原来对
   `Ja`/`Ko` 直接 `throw`（因为当时没有日韩正文），现在改成正常映射到 `'ja'`/`'ko'`，
   顺手删掉了过时的报错分支和它的注释。**我这台机器没有 PowerShell，这处改动完全没跑过
   语法检查**，只是照抄同一份 `.sh` 里已经验证过的逻辑手动搬过去的。请你至少
   `powershell -File windows\scripts\shots\shoot-readme.ps1 -Language Ja` 跑一次，
   确认脚本不报错、`$seedLang`/`$suffix` 取值符合预期（`ja`/`-ja`）。

如果你想要 Windows 端也出一套真实日韩内容的截图（用于社媒素材，不是 README），可以后续
用 `-Language Ja`/`-Language Ko` 各拍一轮；这不是本轮强制项，README 本身不新增日韩行。

## 顺手发现的一处存量疑点（仅供参考，不要求本轮处理）

`windows/scripts/demo-seed.py` 里有一段本地 `CODES = [...]`（写死了各代号的 ms()
时间戳），但紧接着的 `for` 循环实际遍历的是 `DATA.CODES`（共享模块的），不是这个本地
`CODES`——也就是说这个局部变量赋值完全是死代码，从未被使用。功能上无害（真正生效的是
`DATA.CODES`），但读起来容易让人以为改了本地 `CODES` 就能改变行为。是否清理由你判断，
不是本轮任务的一部分，提出来只是避免你以后调试时被这段死代码误导。

## 需要你确认并回报的事项

1. `git pull` 之后，`python3 scripts/check-demo-dataset.py .` 在你的机器上是否全绿；
2. `windows/scripts/demo-seed.py --lang ja` / `--lang ko` 直接跑是否正常（数据库里
   12 节点 + 6 代号，文案是日语/韩语而不是乱码或空值）；
3. `shoot-readme.ps1 -Language Ja` 是否能跑通（哪怕只是语法层面不报错，不强制要求
   实机拍摄）；
4. Windows 侧现有 6 道 CI 关（尤其 Windows Core dotnet smoke、Strings sync）是否受影响——
   理论上不该受影响（这轮没碰 `design/strings.json` 或任何 `.cs`/`.xaml`），但请实跑确认；
5. 如实报告：核实通过就说通过，发现任何不一致（哪怕只是行尾换行符、编码问题）都请说明，
   不要因为"看起来应该没问题"就跳过实跑。

## 本轮不做

- 不新增或修改 `design/strings.json`（本轮没有新增 UI 文案键）；
- 不要求 Windows 侧实拍日韩截图（可选，社媒素材用，不阻塞本轮）；
- 不改 README（日韩数据集不进 README，只服务社媒素材）。
