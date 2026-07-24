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
    public bool EnableKimi { get; set; } = true;
    public bool EnableZcode { get; set; } = false;

    /// <summary>zcode session root — the format is reserved; path is user-provided (PRD 3.1).</summary>
    public string ZcodeSessionRoot { get; set; } = "";

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

    public void Save()
    {
        try
        {
            AppPaths.EnsureDataDirs();
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings", ex);
        }
    }
}
