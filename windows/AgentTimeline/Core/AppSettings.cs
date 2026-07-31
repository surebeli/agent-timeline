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

    // --- Language (i18n) ---

    /// <summary>
    /// 界面语言："System"（跟随系统，默认）/ "ZhHans" / "En" / "Ja" / "Ko"。
    ///
    /// 存字符串而不是 int：设置文件是人可读的，加语言时不希望旧值的语义被序号挪位。
    /// 切换**即时生效**；已入库的历史摘要保持原语言不重跑，但 kind / 代号状态 / 日期
    /// 这些**渲染标签**跟随（它们落库的是枚举值，不是文案）。
    /// </summary>
    public string Language { get; set; } = "System";

    // --- Window / appearance (F5) ---
    public double HoverOpacity { get; set; } = 0.95; // overwritten by tokens on first run
    public double IdleOpacity { get; set; } = 0.25;
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>
    /// 开机自启动，**默认开**。
    ///
    /// 属性初始化器就是 mac <c>UserDefaults.register(defaults:)</c> 的等价语义：
    /// System.Text.Json 反序列化时**缺这个键就保留初始值**，显式写了 <c>false</c> 才是关。
    /// 于是新装用户与从没碰过这项的老用户都落到"默认开"，而关过它的用户不会被升级重新打开。
    /// 实际生效靠 <c>Interop.StartupRegistry.Apply</c> 把这个值推到注册表。
    /// </summary>
    public bool LaunchAtLogin { get; set; } = true;

    // Remembered window bounds in raw (physical) pixels; int.MinValue = not yet saved.
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;
    public int WindowWidth { get; set; } = 0;
    public int WindowHeight { get; set; } = 0;

    /// <summary>面板是否折叠到只剩标题栏（对齐 mac <c>panelCollapsed</c>）。</summary>
    public bool PanelCollapsed { get; set; }

    /// <summary>
    /// 折叠**之前**的窗口高度（物理像素）。必须单独存：折叠后 <see cref="WindowHeight"/>
    /// 存的就是折叠尺寸了，只靠它还原不回去。取值优先级见
    /// <see cref="PanelGeometry.ResolveExpandedHeight"/>（含老用户升级时该字段缺失的情况）。
    /// </summary>
    public int PanelExpandedHeight { get; set; }

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
