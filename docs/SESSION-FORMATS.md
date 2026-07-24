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

## 3. Kimi (Kimi Code CLI)

- **路径**：`~/.kimi/sessions/<project-hash>/<session-uuid>/wire.jsonl`
  - 同目录 `state.json`：`custom_title` 可作 session 标题；
  - `~/.kimi/user-history/<project-hash>.jsonl` 为纯用户输入流水（`{"content": "..."}` 每行），可作交叉校验；
  - `project-hash` 与 cwd 的映射无公开规则；项目名显示取 wire.jsonl 内容推断或 hash 前 8 位。
- **格式**（wire.jsonl）：首行 `{"type":"metadata","protocol_version":...}`；其余 `{timestamp(unix秒.小数), message:{type, payload}}`。
- **用户命令提取**：`message.type=="TurnBegin"` → `payload.user_input[]` 中 `type=="text"` 的 `text` 拼接。
  - 过滤：以 `/` 开头且长度短（如 `/model`）的斜杠命令可标记为 meta 节点（可选展示）。
- **任务完成**：`message.type=="TurnEnd"`（若存在）或下一次 TurnBegin 之前的最后 assistant 输出。

## 4. zcode（预留）

- 本机（2026-07-25）未安装，无样例。适配器占位：
  - 设置中可配 session 根目录 + 格式（jsonl 假设）；
  - 拿到样例后按上述模式补 `ZcodeParser`，协议不变。

## 归一化事件（两端一致）

```
UserCommand { agent, project, sessionId, timestamp, text, sourceFile, sourceOffset }
TaskComplete { agent, sessionId, timestamp, resultLine }
```

## 增量读取约定

- 每文件记录 `(path, byteOffset, inode/fileId)`；新数据从 offset 起按行解析，末尾半行留待下次；
- inode / fileId 变化 → 文件被重建，offset 归零重扫；
- 回填：首扫仅取 mtime 在 N 天内的文件（默认 7）。
