# M3 开工 Prompt（在 Windows 机器的 agent 会话中粘贴使用）

> 用法：Windows 上 clone 仓库后，在仓库根目录启动 Claude Code（或其他 agent CLI），
> 把下面整段粘贴为首条指令。

---

你在一台 Windows 11 机器上，当前目录是仓库 agent-timeline 的根目录（远端 github.com/surebeli/agent-timeline）。产品是双端原生桌面挂件 "Agent Timeline"：半透明时间线跟踪本机 AI agent 的 session 文件，提炼用户命令与任务代号词典。mac 端（macos/）已完成并实机验证；WinUI 端（windows/）源码完整、已通过 CI 的 msbuild 编译硬门禁，但**从未在实机运行过**。

你的任务：完成 **M3 —— Windows 端实机调试**，把"编译通过"推进到"运行验证完毕"。

## 必读（按序，动手前读完）
1. `windows/DEBUG-PLAYBOOK.md` —— 你的任务书：环境准备、种子数据脚本、分层验证清单（§2a–§2e）、五个已知风险点、修复回路；
2. `windows/README.md` —— 工程结构、「已知未验证事项」、更新记录（历次 mac↔win 平台差异全在这里）；
3. `docs/PRD.md` §3.2b（台账视觉语法）与 §3.3（代号生命周期）+ `design/design-tokens.json` —— 行为与视觉的**裁决基准**；
4. `docs/SESSION-FORMATS.md` —— session 解析规范。
视觉参照物：`docs/assets/screenshot-dark.png`（mac 端最终观感）。

## 执行规则
- 严格按 PLAYBOOK 从 §0 到 §2e 逐层推进；每个 checkbox 实测后**直接在 DEBUG-PLAYBOOK.md 里勾选并注记**（✅ / ❌ + 现象一句话）；
- 构建只用 `msbuild ... /restore /p:Configuration=Release /p:Platform=x64`，**禁止 `dotnet build`**（PRI 任务缺失，原因见 PLAYBOOK §0）；
- 时间线为空时先跑 `windows\scripts\seed-fixture-session.ps1`（无需安装任何 agent），`-Append` 验实时 tail，`-Bulk 500` 验滚动性能；
- 修复只改 `windows/` 内文件；若判断问题根源在 `design/design-tokens.json`、`docs/SESSION-FORMATS.md` 或需要 mac 端联动的双端语义，**停下来向我报告方案，不要单方面改共享层**；
- 每个修复完成后：重新 msbuild + `dotnet run --project windows/CoreSmokeTest -c Release`（85 断言须保持全绿）+ 中文 commit（风格参考 `git log`，结尾保留 Co-Authored-By 行的惯例可省）；阶段性 push，CI 四道关自动回归；
- UI 行为争议以 mac 参照物裁决；平台确实无法 1:1 的（如原生粘性 section header），在 README 更新记录里补 deviation 说明，不硬凑；
- 崩溃/挂起排障顺序：`%LOCALAPPDATA%\AgentTimeline\` 下的 store/settings 状态 → Visual Studio 调试器附加 → Windows 事件查看器；三步内无解就带证据向我报告。

## 最终交付
1. `windows/DEBUG-PLAYBOOK.md` 清单全部勾选、注记完整；
2. 全部修复 commits 已 push 且 CI 全绿；
3. `CHANGELOG.md` 新增 0.2.x「M3 Windows 实机验证完成」条目，附平台 deviation 终版清单；
4. 总结报告：通过项 / 修复项（各附一句根因）/ 遗留项（按优先级排序，注明建议处理时机）。
