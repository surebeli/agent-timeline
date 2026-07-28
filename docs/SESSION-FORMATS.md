# Session 文件格式规范（双端解析器共同依据）

以下格式均已在真实数据上验证（2026-07-25，macOS）。Windows 端路径把 `~` 换成 `%USERPROFILE%`，分隔符换成 `\`。

## 1. Claude Code

- **路径**：`~/.claude/projects/<project-slug>/<session-uuid>.jsonl`
  - `project-slug` 是 cwd 的转义（`/`→`-`，如 `-Users-litianyi-Documents-Code-foo`）；真实 cwd 以行内 `cwd` 字段为准。
- **格式**：每行一个 JSON 对象，`type` 区分：`user` / `assistant` / `attachment` / `system` / `file-history-snapshot` / `mode` / `queue-operation` 等。
- **用户命令提取**（`type == "user"`）：
  - 跳过 `isMeta == true` 的行；
  - 跳过 `isSidechain == true`（子 agent 对话）；
  - `message.content` 为 string 时直接取；为数组时取其中 `type=="text"` 段拼接，**跳过** `tool_result` 段（那是工具回包不是用户输入）；
  - 跳过以 `<local-command-caveat>` / `<command-name>` / `<system-reminder>` 开头的内容（本地命令回显，非用户 prompt）；`<command-name>/xxx</command-name>` 形式可提取为 `/xxx` 命令节点（可选）；
  - 字段：`timestamp`（ISO8601）、`cwd`、`gitBranch`、`sessionId`、`uuid`、`version`。
- **执行结果**：最后一条 `type=="assistant"` 的 `message.content[].text` 首段可作为 resultLine 候选。

## 2. Codex

- **路径**：`~/.codex/sessions/YYYY/MM/DD/rollout-<ts>-<uuid>.jsonl`
- **格式**：每行 `{timestamp, type, payload}`；`type` ∈ `session_meta` / `event_msg` / `response_item` / `turn_context` / `compacted`。
- **session 元信息**（`type=="session_meta"`）：`payload.id`、`payload.cwd`、`payload.cli_version`。
- **用户命令提取**：`type=="event_msg" && payload.type=="user_message"` → `payload.message`（string）。
  - 过滤：以 `<user_instructions>`、`<environment_context>` 开头的为环境注入，跳过。
- **任务完成**：`payload.type=="task_complete"` → `payload.last_agent_message` 作为 resultLine。

## 3. Grok Build

已在真实数据上验证（2026-07-28，Windows，本机 87 个 session / 27724 行）。

- **路径**：`~/.grok/sessions/<URL 编码的 cwd>/<session-uuid>/updates.jsonl`
  - 目录名是**百分号编码的工作目录绝对路径**（`F%3A%5Cworkspace%5Cproject%5Chawk-watcher`
    → `F:\workspace\project\hawk-watcher`）；文件内**没有任何 cwd 字段**，项目名只能由
    目录名解码后取末段。mac 侧同理（`%2FUsers%2F…` → `/Users/…`）；
  - `sessionId` 取 `params.sessionId`，回退用目录名——实测 87/87 两者恒等，且
    **每个文件有且只有一个 sessionId**（无 Kimi 那类子 agent 串台风险）；
  - ⚠ **必须锚定到 `updates.jsonl`**：同一棵树下并存 6 种 `.jsonl`
    （`chat_history` 91 / `events` 91 / `updates` 87 / `rewind_points` 81 /
    `hunk_records` 4 / `prompt_history` 3），宽松匹配会重复摄取。
- **格式**：每行一条 ACP（Agent Client Protocol）通知
  `{timestamp, method:"session/update", params:{sessionId, update:{sessionUpdate, …}}}`。
  - ⚠ `timestamp` 是 **unix 整秒**（int），**不是 ISO8601**——两端的
    `TryParseIsoTimestamp` / `ParserSupport.timestamp` 都解不了，需专走数值分支。
- **用户命令提取**：`update.sessionUpdate == "user_message_chunk"` →
  `update.content.text`。
  - 名字里虽有 chunk，但**一条即一条完整消息**，不需拼接（实测 92 条各自完整）；
  - ⚠ **不要依赖 `content._meta.displayText`**：92 条里只有 1 条带该字段；
  - 该通道里的文本**已经去过壳**——`chat_history.jsonl` 里的 `<user_query>` /
    `<user_info>` / `<skill_information>` 包装在此不出现（实测各 0 命中）。
- **agent 回复**（结果行 + 代号挖掘）：取 `turn_completed` 之前**最后一条**
  `agent_message_chunk` 的 `content.text`。
  - Grok 在工具调用之间会输出进度旁白，一个轮次内有多条 `agent_message_chunk`
    （实测 532 条对 57 个 `turn_completed`），只有最后一条是给用户的答复；
  - `task_completed` 是**子任务/工具**完成，不是轮次完成，不可当结果行。
- **全部忽略**：`tool_call` / `tool_call_update` / `hook_execution` /
  `agent_thought_chunk`（思考过程）/ `plan` / `task_backgrounded` /
  `task_completed` / `session_recap`。
- **L1 过滤**：`<system-reminder>` 开头的是后台任务回执（实测 4 条），非人类输入，
  按与 Claude 侧同一规则跳过。
- ⚠ **已知未决**：本机 92 条用户消息中 85 条是编排器派发的子 agent 任务书
  （`# ⚠ EXECUTION MODE … You were dispatched by …`），只有 3 条是真人手打。
  真人会话与被派发会话在 `updates.jsonl` 里**结构完全一致**，无协议级判据可区分
  （唯一差异是模型不同：Composer vs Grok，不可作依据）。当前**不做过滤**，详见
  `docs/TEXT-NORMALIZATION.md §4.2c`。
- 状态：**双端均已实现**（2026-07-28），语义按本节对齐。

## 4. Kimi Code

> ⚠ **2026-07-28 换代**：目录从 `~/.kimi/sessions` 迁到 `~/.kimi-code/sessions`
> （旧目录留有 `.migrated-to-kimi-code` 标记），且 wire 协议 **1.10 → 1.4**，
> 消息类型全部重写——旧的 `TurnBegin` / `ContentPart` 已不存在。
> 本节据本机 44 个真实 session 实证重写；**旧布局不再支持**。

- **路径**：`~/.kimi-code/sessions/wd_<项目>_<12hex>/session_<uuid>/agents/main/wire.jsonl`
  - `sessionId` 取 `session_<uuid>` 目录名；
  - **项目名直接取自目录名**（新格式的关键改进：旧版 project-hash 无公开映射规则，
    只能显示 `kimi:1a2b3c4d`）——剥掉 `wd_` 前缀与末段 `_<12hex>`；项目名本身可能
    含下划线（`wd_hawk_agent-rs_dd8b1189a258` → `hawk_agent-rs`），故只剥固定的
    前缀与末段 hash，剥不掉就原样用目录名；
  - `agents/` 下当前只见 `main`（44/44）；未来若出现子 agent 目录，按 Claude
    sidechain 同理处理（子 agent 输出不是主会话的结果）。
- **格式**：每行一个 JSON 对象，**顶层 `type`**（不再有嵌套的 `message.type`）；
  `time` 为**毫秒** epoch。
- **用户命令提取**：`type == "turn.prompt"` 且 `origin.kind == "user"` →
  `input[]` 中 `type == "text"` 的 `text` 拼接。
  - ⚠ **不要用** `context.append_message` role=user：那条通道混着注入上下文
    （实测 85 条 vs 真实 prompt 39 条）；
  - 过滤：裸斜杠命令（如 `/model`）是 UI 动作，跳过。
- **agent 回复**（结果行 + 代号挖掘）：`type == "context.append_loop_event"` 且
  `event.type == "content.part"` 且 **`event.part.type == "text"`** → `event.part.text`。
  - ⚠ **必须排除 `part.type == "think"`**：那是模型思考过程不是答复
    （实测 324 条 think vs 49 条 text）。
- **全部忽略**：`metadata` / `config.update` / `tools.*` / `permission.*` /
  `context.append_message` / `usage.record`，以及 `step.begin`/`step.end`/
  `tool.call`/`tool.result` 等其余 loop 事件。

## 5. ZCode（Z Code CLI）

已在真实数据上验证（2026-07-27，Windows，ZCode 3.5.2）。

- **路径**：`~/.zcode/cli/agents/sess_<uuid>/agent_<uuid>/transcript.jsonl`
  - 每次任务派发一个 `agent_<uuid>` 目录；同目录 `metadata.json` 为 sidecar：
    `cwd`（项目名取末段）、`description`、`status`、`prompt`、`createdAt`；
  - 根目录可在设置中覆盖（默认即上述路径）。
- **格式**：每行 `{id, sessionId, turnId, type, timestamp(ISO8601), sequenceNumber, payload}`。
- **任务命令提取**：`type=="turn_started"` → `payload.input`（string）；空白跳过。
- **任务完成**：`type=="turn_complete"` → `payload.response`，首行作 resultLine，
  全文参与代号挖掘。
- 过程事件（`model_streaming` / `model_request` / `tool_call_scheduled` /
  `streaming_tool_ledger_updated` 等）全部忽略。
- **语义注**：agents 目录记录的是任务派发（含子 agent），非主会话人机对话——
  时间线粒度为「一次任务 = 一个节点」；主会话仅在派发 agent 时产生节点。
- 状态：**双端均已实现**（Windows 2026-07-27 / macOS 2026-07-28），语义按本节对齐。

## 归一化事件（两端一致）

```
UserCommand { agent, project, sessionId, timestamp, text, sourceFile, sourceOffset }
TaskComplete { agent, sessionId, timestamp, resultLine }
```

## 增量读取约定

- 每文件记录 `(path, byteOffset, inode/fileId)`；新数据从 offset 起按行解析，末尾半行留待下次；
- inode / fileId 变化 → 文件被重建，offset 归零重扫；
- 回填：首扫仅取 mtime 在 N 天内的文件（默认 7）。
