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

    // Session roots per docs/SESSION-FORMATS.md (~ → %USERPROFILE%).
    public static string ClaudeProjectsRoot => Path.Combine(UserProfile, ".claude", "projects");
    public static string CodexSessionsRoot => Path.Combine(UserProfile, ".codex", "sessions");
    public static string KimiSessionsRoot => Path.Combine(UserProfile, ".kimi", "sessions");

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
