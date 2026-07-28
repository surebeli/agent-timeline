using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentTimeline.Core;

public enum SummaryEngineKind
{
    Cli,       // default: reuse the locally installed agent CLI (claude -p / codex exec)
    Provider,  // custom OpenAI-compatible endpoint
    Rule,      // no LLM at all
}

/// <summary>
/// Persisted user settings (PRD F6). Stored as JSON at %LOCALAPPDATA%\AgentTimeline\settings.json.
/// Defaults come from design/design-tokens.json where applicable (opacity levels, panel size).
/// </summary>
public sealed class AppSettings
{
    // --- Summary engine (F4 / F6) ---
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SummaryEngineKind Engine { get; set; } = SummaryEngineKind.Cli;

    /// <summary>"auto" | "claude" | "codex" — which CLI the CliSummarizer should invoke.</summary>
    public string CliCommand { get; set; } = "auto";

    public string ProviderBaseUrl { get; set; } = "";
    public string ProviderApiKey { get; set; } = "";
    public string ProviderModel { get; set; } = "";

    // --- Window / appearance (F5) ---
    public double HoverOpacity { get; set; } = 0.95; // overwritten by tokens on first run
    public double IdleOpacity { get; set; } = 0.25;
    public bool AlwaysOnTop { get; set; } = true;

    // Remembered window bounds in raw (physical) pixels; int.MinValue = not yet saved.
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;
    public int WindowWidth { get; set; } = 0;
    public int WindowHeight { get; set; } = 0;

    // --- Watching / backfill (F1) ---
    public int BackfillDays { get; set; } = 7;
    public bool EnableClaude { get; set; } = true;
    public bool EnableCodex { get; set; } = true;
    public bool EnableGrok { get; set; } = true;
    public bool EnableKimi { get; set; } = true;
    public bool EnableZcode { get; set; } = true;

    /// <summary>
    /// zcode session root；空 = 默认 %USERPROFILE%\.zcode\cli\agents（实机确认，
    /// AppPaths.ZcodeAgentsRootDefault），填写则覆盖。
    /// </summary>
    public string ZcodeSessionRoot { get; set; } = "";

    // --- Codename lifecycle (F3) ---

    /// <summary>
    /// Highest codename-replay version already completed (mac: UserDefaults
    /// "codenameReplayVersion"). TimelineCoordinator replays stored history when this is
    /// below its current version, and writes the marker only AFTER the replay finishes.
    /// </summary>
    public int CodenameReplayVersion { get; set; }

    // ------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load settings, falling back to defaults", ex);
        }
        return new AppSettings();
    }

    // UI 线程与重放/watcher 后台线程都会 Save；无锁并发写会互相覆盖或截断
    // （最坏丢重放标记/窗口位置）。锁内序列化 + 临时文件原子替换。
    private static readonly object SaveGate = new();

    public void Save()
    {
        try
        {
            AppPaths.EnsureDataDirs();
            lock (SaveGate)
            {
                var tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
                File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings", ex);
        }
    }
}
