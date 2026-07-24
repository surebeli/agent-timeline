# Agent Timeline

常驻桌面的半透明时间线挂件：实时跟踪本机 AI agent（Claude Code / Codex / Kimi / zcode 预留）的 session 文件，把**你提交过的每条命令**提炼成时间线节点——标题、关键点/需求点/任务点、**任务代号词典**（自动登记、点击回溯原始定义）——解决长周期任务里"代号忘了是啥、翻 session 找不到"的问题。

- 📄 需求：[docs/PRD.md](docs/PRD.md) · 架构：[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) · 解析规范：[docs/SESSION-FORMATS.md](docs/SESSION-FORMATS.md)
- 🎨 双端共享视觉规范：[design/design-tokens.json](design/design-tokens.json)（唯一事实源；mac 构建期嵌入二进制，win 生成 XAML 资源）

## macOS（M1，已完成）

技术栈：Swift + SwiftUI + AppKit（NSPanel 非激活浮窗 / NSVisualEffectView 毛玻璃 / FSEvents / SQLite），零第三方依赖。

```bash
cd macos
scripts/build-app.sh release        # 产出 macos/dist/AgentTimeline.app
cp -R dist/AgentTimeline.app /Applications/   # 建议装到 /Applications（避免 ~/Documents 的 TCC 授权）
open /Applications/AgentTimeline.app
swift test                          # 解析器单元测试
```

- 入口在 **menu bar**（时钟图标），无 Dock 图标；菜单含 显示/隐藏、窗口置顶、设置、退出。
- 浮窗：hover ≈95% 不透明可读，失焦 ≈25% 不遮挡（设置可调）；置顶开关；全部文本可划选复制；点击不抢占当前 app 焦点；窗口位置尺寸记忆。
- 时间线：最新在上；节点=时间+项目+agent 徽标+标题+关键点；代号 chip 点击看定义/首次出现/出现次数，可跳转定义节点；展开看原始命令全文与执行结果一句话。
- 摘要引擎（设置里切换）：
  1. **复用本机 CLI**（默认，零配置）：调 `claude -p`（备选 `codex exec`）headless，模型默认 `haiku`；在专用 scratch 目录运行，自身 session 不会污染时间线；
  2. **自定义 Provider**：OpenAI 兼容 `/chat/completions`（base URL + key + model）；
  3. **纯规则**：不调模型。LLM 失败自动降级规则摘要，节点先上屏后原位升级。
- 数据：`~/Library/Application Support/AgentTimeline/store.sqlite`（节点/代号/文件偏移；摘要按命令内容 hash 去重，不重复调用）。

### 已知事项

- 首次以 `open` 启动若长时间无内容：检查是否有系统授权弹窗排队（macOS 的安全弹窗是串行的，一个未处理会卡住后面所有，包括 headless claude 的凭据授权）。
- Kimi 节点少是正常的：回填只扫“最近 N 天”（默认 7，设置可调）。
- zcode：本机未装、无样例。设置里开启并填 session 根目录后，拿到样例文件补 `ZcodeParser.parse`（协议不变，参考其他三个解析器）。

## Windows（M3 脚手架，待 win 机器编译调试）

技术栈：WinUI 3（Windows App SDK / .NET 8 / C#）+ H.NotifyIcon 托盘 + DesktopAcrylic 半透明 + Topmost。完整源码在 [windows/](windows/)，Core 解析层已在 mac 上用 `dotnet` 编译通过并过冒烟测试；UI 层需在 Windows + Visual Studio 2022 首次编译调试，步骤见 [windows/README.md](windows/README.md)。

## 里程碑

- **M1** mac MVP ✅（本仓库当前状态）
- **M2** 搜索过滤增强、代号词典管理界面、多项目视图
- **M3** Windows 端编译调试与双端视觉对齐验收
