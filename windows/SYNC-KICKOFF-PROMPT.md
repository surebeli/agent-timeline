# Windows 同步开工 Prompt（截图刷新：补日/韩 + 追平版本 · 2026-08-04）

> 用法：在 Windows 机器的仓库根目录启动 agent 会话，把下面 `---` 之间的整段粘贴为首条指令。
>
> 上一轮（代号词典关键字搜索）已完成并对账，见 git log 里本文件的上一版。v0.7.6 已发布。
> 本文件整体替换为本轮内容。

---

你在一台 Windows 11 机器上，当前目录是仓库 agent-timeline 根目录
（远端 **github.com/litianyi-007/agent-timeline** —— 注意仓库地址变了，见下面「零」，
先 `git pull` 到最新 main）。产品是双端桌面挂件「Agent Timeline」，两端跑在 CI 六道关下，
最新发布 v0.7.6。

**本轮任务：把 Windows 侧的 README 截图刷新到 v0.7.6，并补上日语/韩语两套。**
用户直接需求（原话「为仓库增加 日文和韩文的readme，配合替换对应语种的截图」）——
mac 侧已做完，Windows 侧的图我这边拍不了，交给你。

## 零、先注意：仓库地址已变更

远端从 `surebeli/agent-timeline` 改成了 **`litianyi-007/agent-timeline`**。旧地址目前
仍会自动重定向（GitHub 的改名跳转），但仓库内所有引用已经全部替换过一遍（8 个文件），
你 pull 下来就是新的。**如果你本地 clone 的 remote 还是旧地址**，顺手改一下：

```
git remote set-url origin https://github.com/litianyi-007/agent-timeline
```

## 一、mac 侧这轮做了什么

1. **新增 `README.ja.md` / `README.ko.md`**（全文翻译，不是机翻），四个 README 顶部
   都加了语言切换栏：`[中文] · [English] · [日本語] · [한국어]`；
2. **四语 macOS 截图全部重拍到 v0.7.6**，每语 7 张：
   `screenshot-dark-<lang>.png`（首图）、`screenshot-macos-{timeline,projects,dictionary,settings}-<lang>.png`、
   `onboarding-{1-overview,2-collapse}-<lang>.png`。中文无后缀，其余 `-en`/`-ja`/`-ko`；
3. **`macos/scripts/shots/onboarding-spec.py` 扩到四语**（原来只有 zh/en），新增
   `SUFFIX = {"zh": "", "en": "-en", "ja": "-ja", "ko": "-ko"}`，与 `shoot-readme.sh`
   的 SUFFIX 分支同口径；
4. 顺手修了三处 README 里的过时数字：`swift test` 81 → **106**、Windows 冒烟
   400 → **463**、strings 69 键 → **74 键**。

### 为什么连中英也重拍了（对你这轮有直接影响）

原来的中英截图摄于 **v0.7.0**，此后 UI 变过两次：

- **v0.7.2** 设置窗新增「开机自启动」开关；
- **v0.7.6** 词典面板新增**搜索框**。

也就是说 v0.7.0 的老图**看不到这两个新 UI**。mac 侧四语重拍后都有了，**而 Windows 侧
的图还是 v0.6.0 的**，比 mac 还旧一档。我已经在四个 README 的截图区下面如实写了警示：

> ⚠️ 两行版本不同步：mac 一行是 v0.7.6 重拍的，词典面板里能看到搜索框；Windows 一行
> 仍是 v0.6.0 的旧图，看不到搜索框、设置窗也没有 v0.7.2 加的开机自启动开关。

**这条警示就是本轮要消除的东西。**

## 二、要你做的

### 1. Windows 截图重拍到 v0.7.6，补齐四语

用 `windows/scripts/shots/shoot-readme.ps1`（已支持 `-Language ZhHans|En|Ja|Ko`，
上一轮我改的、你核实过）四语各跑一轮：

```
powershell -ExecutionPolicy Bypass -File windows\scripts\shots\shoot-readme.ps1 -Language ZhHans -Install
powershell -ExecutionPolicy Bypass -File windows\scripts\shots\shoot-readme.ps1 -Language En     -Install
powershell -ExecutionPolicy Bypass -File windows\scripts\shots\shoot-readme.ps1 -Language Ja     -Install
powershell -ExecutionPolicy Bypass -File windows\scripts\shots\shoot-readme.ps1 -Language Ko     -Install
```

产出应为 `screenshot-windows-{timeline,projects,dictionary}<suffix>.png`
+ `screenshot-windows-settings<suffix>.png`。**注意现在仓库里的
`screenshot-windows-settings.png` 只有中文一份、没有 `-en`**（历史遗留），
这轮四语补齐正好一并解决。

**验收点**：拍完的词典图里**必须能看到搜索框**，设置图里**必须能看到开机自启动开关**
——这是「确实是 v0.7.6 的图」的判据，比对文件时间戳可靠。

### 2. 改四个 README 里的 Windows 图引用

四个 README 的「Windows 实机一览」目前的引用状态：

| README | 现在引的 | 应改成 |
|---|---|---|
| `README.md`（中） | `screenshot-windows-*.png` | 不变（中文无后缀） |
| `README.en.md` | `screenshot-windows-*-en.png` | 不变 |
| `README.ja.md` | `screenshot-windows-*-en.png`（**临时借用英文图**） | `-ja` |
| `README.ko.md` | `screenshot-windows-*-en.png`（**临时借用英文图**） | `-ko` |

日/韩两版现在借用英文图是**我有意为之的临时状态**，README 里也写了如实说明：

> ⚠️ Windows 行是英语 UI 截图：演示数据集四语齐全，但 Windows 实机的日/韩拍摄尚未进行。

你拍完 `-ja`/`-ko` 之后，把这两版的引用改过去，**并删掉上面那条「借用英文图」的警示**
（日/韩 README 各一条，在截图区下方的引用块里）。

### 3. 删掉「两行版本不同步」的警示

四个 README 截图区下方都有那条 ⚠️ 警示（中/英/日/韩各一条，措辞按各自语言）。
Windows 图追平 v0.7.6 之后**这条就不成立了，四个文件都要删**。同时把版本注记里的
`Windows 摄于 v0.6.0` 改成 `v0.7.6`。

⚠️ 日/韩两版的警示是**两条**（版本不同步 + 借用英文图），别只删一条。

### 4. 检查画布尺寸是否仍然对齐

README 靠「两端画布宽高比逐位相同」让 Windows 行与 macOS 行等高。现有注记写的是：
mac `1618×1352`（Retina 2x）、Windows `859×676`（100% 缩放，dip 几何一致、像素密度一半）。

mac 这轮重拍后仍是 **1618×1352**（我确认过）。你拍完确认 Windows 仍是 859×676；
**如果不是**，说明中间有布局改动影响了并集宽度，先停下来报告，别直接改注记数字。

## 需要你确认并回报的事项

1. 四语 Windows 截图产出的实际尺寸（应为 859×676），以及词典图有搜索框、设置图有
   开机自启动开关的目视确认；
2. 四个 README 的 Windows 引用与警示删除是否都改到位（日/韩各两条警示）；
3. `screenshot-windows-settings-{en,ja,ko}.png` 是否补齐（此前只有中文一份）；
4. CI 六道关是否全绿。

## 本轮不做

- **不动 mac 侧的任何截图与 README 的 mac 部分**——那是我这轮刚拍的 v0.7.6，已验证；
- 不改 `design/` 下任何共享文件（这轮没有新增文案键）；
- 不升版本号、不打 tag、不发 release——发布时机由用户定。
