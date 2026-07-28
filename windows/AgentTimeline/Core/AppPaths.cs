namespace AgentTimeline.Core;

/// <summary>Well-known filesystem locations (Windows equivalents of the mac paths in docs).</summary>
public static class AppPaths
{
    public static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>%LOCALAPPDATA%\AgentTimeline — settings, SQLite DB, logs, summarizer cwd.</summary>
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentTimeline");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string DatabaseFile => Path.Combine(DataDir, "timeline.db");
    public static string LogFile => Path.Combine(DataDir, "logs", "app.log");

    /// <summary>
    /// Dedicated working directory for headless `claude -p` calls, so the summarizer's own
    /// session files land under a slug we can recognize and EXCLUDE from watching
    /// (otherwise summarizing would generate new session lines → infinite feedback loop).
    /// </summary>
    public static string SummarizerWorkDir => Path.Combine(DataDir, "summarizer");

    /// <summary>
    /// 这个 cwd 是不是我们自己的摘要器 scratch 目录（对齐 mac `AppSettings.summarizerScratchDir`
    /// 判定）。SessionWatcher 的路径级排除只对 claude 有效——它靠「项目 slug 里含
    /// AgentTimeline+summarizer」认出来；codex 的 rollout 落在 `~\.codex\sessions\YYYY\MM\DD\`
    /// 下，路径里永远不含这两个词，只能靠 `session_meta.payload.cwd` 认。摘要引擎解析到
    /// `codex exec` 时，不认就会把自己发出的每条摘要 prompt 当成用户命令收进时间线（自摄取回路）。
    ///
    /// 比较口径比 mac 的字符串全等宽一点：归一分隔符 + 去尾分隔符 + 大小写不敏感，
    /// 因为 Windows 路径本就大小写不敏感，而各家 CLI 回写 cwd 时 `\` / `/` 混用。
    /// </summary>
    public static bool IsSummarizerWorkDir(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return false;
        return string.Equals(NormalizeDir(cwd!), NormalizeDir(SummarizerWorkDir),
            StringComparison.OrdinalIgnoreCase);

        static string NormalizeDir(string p) => p.Replace('\\', '/').TrimEnd('/');
    }

    // Session roots per docs/SESSION-FORMATS.md (~ → %USERPROFILE%).
    public static string ClaudeProjectsRoot => Path.Combine(UserProfile, ".claude", "projects");
    public static string CodexSessionsRoot => Path.Combine(UserProfile, ".codex", "sessions");
    /// <summary>Kimi Code：2026-07-28 起会话落在 `.kimi-code`（旧的 `.kimi` 不再支持）。</summary>
    public static string KimiSessionsRoot => Path.Combine(UserProfile, ".kimi-code", "sessions");

    /// <summary>zcode agent 任务会话默认根（实机确认 2026-07-27）；settings 可覆盖。</summary>
    public static string ZcodeAgentsRootDefault => Path.Combine(UserProfile, ".zcode", "cli", "agents");

    public static void EnsureDataDirs()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
        Directory.CreateDirectory(SummarizerWorkDir);
    }
}

/// <summary>Minimal file logger; keep the scaffold debuggable on the user's Windows machine.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    AppPaths.LogFile,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
