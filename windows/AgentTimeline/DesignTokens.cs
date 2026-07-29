using System.Text.Json;
using AgentTimeline.Core;
using Windows.UI;

namespace AgentTimeline;

/// <summary>
/// Runtime accessor for design/design-tokens.json (the canonical cross-platform visual spec).
/// The file is shipped as Content at Assets\design-tokens.json — it is a COPY of the repo-root
/// design/design-tokens.json; that root file is the single source of truth.
///
/// XAML-side values (brushes, sizes, radii) live in Themes\Tokens.xaml which is generated
/// from the same JSON; this class serves the values needed from code-behind
/// (opacity levels, animation duration, panel geometry, agent badge colors).
///
/// NOTE on color format: the token file uses #RRGGBB or #RRGGBBAA (alpha LAST),
/// while XAML uses #AARRGGBB. ParseColor handles the token order.
/// </summary>
public sealed class DesignTokens
{
    public double HoverOpacity { get; private set; } = 0.95;
    public double IdleOpacity { get; private set; } = 0.25;
    public int TransitionMs { get; private set; } = 180;

    /// <summary>
    /// 面板**变暗**（指针移出/失活）的时长，比变亮长得多——「快淡入、慢淡出」：
    /// 指针一进来要立刻可读，移开时则从容化开，避免"看到一半就唰地消失"。
    /// 曲线也随方向换（见 OpacityAnimator）：只拉长时长而不换曲线，ease-out 会把
    /// 绝大部分变化挤在前段，观感反而变成"唰一下再慢慢爬"。
    /// </summary>
    public int TransitionOutMs { get; private set; } = 500;

    // --- 台账 (dual-ink ledger) values consumed from code-behind / view-models ---
    public double AnchorWashOpacity { get; private set; } = 0.08;
    public int HoverFadeMs { get; private set; } = 120;
    public int CopyMorphMs { get; private set; } = 800;
    public int CommandCollapsedLines { get; private set; } = 3;
    public int KeypointsCollapsedLines { get; private set; } = 1;

    public int PanelDefaultWidth { get; private set; } = 340;
    public int PanelMinWidth { get; private set; } = 280;
    public int PanelMaxWidth { get; private set; } = 560;
    public int PanelDefaultHeight { get; private set; } = 640;

    /// <summary>
    /// Set once at launch from Application.RequestedTheme; picks the light/dark variant of
    /// dual-color tokens for code-built UI (flyouts, chip badges). The XAML side keeps using
    /// ThemeResource lookups from Themes/Tokens.xaml.
    /// </summary>
    public bool DarkTheme { get; set; } = true;

    private readonly Dictionary<string, Color> _agentColors = new();
    private readonly Dictionary<string, Color> _kindColors = new();
    private readonly Dictionary<string, (Color Light, Color Dark)> _dualColors = new();

    private static readonly Color FallbackGray = Color.FromArgb(255, 128, 128, 128);

    public Color AgentColor(AgentKind kind) =>
        _agentColors.TryGetValue(kind.Key(), out var c) ? c : FallbackGray;

    /// <summary>color.kind[label] — the phase-tag color for a NodeKind label (需求/任务/…).</summary>
    public Color? KindColor(string? label) =>
        label is not null && _kindColors.TryGetValue(label, out var c) ? c : null;

    /// <summary>A dual light/dark token color (accent / resultLine / statusChanged / …).</summary>
    public Color DualColor(string name) =>
        _dualColors.TryGetValue(name, out var pair) ? (DarkTheme ? pair.Dark : pair.Light) : FallbackGray;

    /// <summary>
    /// Codename status → display color, mirroring the mac chip/dictionary mapping:
    /// 完成 → resultLine, 变更 → statusChanged, 进行中 → accent, else secondary text.
    /// </summary>
    public Color StatusColor(CodenameStatus? status) => status switch
    {
        CodenameStatus.Completed => DualColor("resultLine"),
        CodenameStatus.Changed => DualColor("statusChanged"),
        CodenameStatus.Active => DualColor("accent"),
        _ => DualColor("textSecondary"),
    };

    /// <summary>The ✓/△/▶ chip badge for statuses that have one (定义/提及 render none).</summary>
    public static string StatusGlyph(CodenameStatus? status) => status switch
    {
        CodenameStatus.Completed => "✓",
        CodenameStatus.Changed => "△",
        CodenameStatus.Active => "▶",
        _ => "",
    };

    public static DesignTokens Load()
    {
        var tokens = new DesignTokens();
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "design-tokens.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.TryGetProperty("opacity", out var opacity))
            {
                tokens.HoverOpacity = GetDouble(opacity, "hover", tokens.HoverOpacity);
                tokens.IdleOpacity = GetDouble(opacity, "idle", tokens.IdleOpacity);
                tokens.TransitionMs = (int)GetDouble(opacity, "transitionMs", tokens.TransitionMs);
                tokens.TransitionOutMs = (int)GetDouble(opacity, "transitionOutMs", tokens.TransitionOutMs);
                tokens.AnchorWashOpacity = GetDouble(opacity, "anchorWash", tokens.AnchorWashOpacity);
                tokens.HoverFadeMs = (int)GetDouble(opacity, "hoverFadeMs", tokens.HoverFadeMs);
            }

            if (root.TryGetProperty("motion", out var motion))
            {
                tokens.CopyMorphMs = (int)GetDouble(motion, "copyMorphMs", tokens.CopyMorphMs);
            }

            if (root.TryGetProperty("lineLimit", out var lineLimit))
            {
                tokens.CommandCollapsedLines =
                    (int)GetDouble(lineLimit, "commandCollapsed", tokens.CommandCollapsedLines);
                tokens.KeypointsCollapsedLines =
                    (int)GetDouble(lineLimit, "keypointsCollapsed", tokens.KeypointsCollapsedLines);
            }

            if (root.TryGetProperty("panel", out var panel))
            {
                tokens.PanelDefaultWidth = (int)GetDouble(panel, "defaultWidth", tokens.PanelDefaultWidth);
                tokens.PanelMinWidth = (int)GetDouble(panel, "minWidth", tokens.PanelMinWidth);
                tokens.PanelMaxWidth = (int)GetDouble(panel, "maxWidth", tokens.PanelMaxWidth);
                tokens.PanelDefaultHeight = (int)GetDouble(panel, "defaultHeight", tokens.PanelDefaultHeight);
            }

            if (root.TryGetProperty("color", out var color))
            {
                if (color.TryGetProperty("agentBadge", out var badges))
                {
                    foreach (var prop in badges.EnumerateObject())
                    {
                        tokens._agentColors[prop.Name] = ParseColor(prop.Value.GetString() ?? "#808080");
                    }
                }
                if (color.TryGetProperty("kind", out var kinds))
                {
                    foreach (var prop in kinds.EnumerateObject())
                    {
                        tokens._kindColors[prop.Name] = ParseColor(prop.Value.GetString() ?? "#808080");
                    }
                }
                // Dual light/dark tokens needed by code-built UI (chip badges, flyouts),
                // plus the ledger surfaces so DualColor covers every dual-color block.
                foreach (var name in new[]
                {
                    "accent", "resultLine", "statusChanged", "codenameChipText",
                    "textSecondary", "textTertiary",
                    "commandText", "commandBg", "derivedBg", "derivedRule",
                    "entryHover", "entryDivider",
                    "dayHeaderText", "dayHeaderRule", "dayHeaderBg",
                    "panelScrim", "surfaceStroke",
                })
                {
                    if (color.TryGetProperty(name, out var dual) &&
                        dual.ValueKind == JsonValueKind.Object)
                    {
                        tokens._dualColors[name] = (
                            ParseColor(GetStringProp(dual, "light") ?? "#808080"),
                            ParseColor(GetStringProp(dual, "dark") ?? "#808080"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load design tokens, using built-in defaults", ex);
        }
        return tokens;
    }

    private static double GetDouble(JsonElement parent, string name, double fallback) =>
        parent.TryGetProperty(name, out var el) && el.TryGetDouble(out var v) ? v : fallback;

    private static string? GetStringProp(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;

    /// <summary>Parses "#RRGGBB" or "#RRGGBBAA" (token order: alpha last).</summary>
    public static Color ParseColor(string hex)
    {
        var s = hex.TrimStart('#');
        byte r = 0, g = 0, b = 0, a = 255;
        if (s.Length >= 6)
        {
            r = Convert.ToByte(s[..2], 16);
            g = Convert.ToByte(s[2..4], 16);
            b = Convert.ToByte(s[4..6], 16);
        }
        if (s.Length == 8)
        {
            a = Convert.ToByte(s[6..8], 16);
        }
        return Color.FromArgb(a, r, g, b);
    }
}
