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

    public int PanelDefaultWidth { get; private set; } = 340;
    public int PanelMinWidth { get; private set; } = 280;
    public int PanelMaxWidth { get; private set; } = 560;
    public int PanelDefaultHeight { get; private set; } = 640;

    private readonly Dictionary<string, Color> _agentColors = new();

    public Color AgentColor(AgentKind kind) =>
        _agentColors.TryGetValue(kind.Key(), out var c) ? c : Color.FromArgb(255, 128, 128, 128);

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
            }

            if (root.TryGetProperty("panel", out var panel))
            {
                tokens.PanelDefaultWidth = (int)GetDouble(panel, "defaultWidth", tokens.PanelDefaultWidth);
                tokens.PanelMinWidth = (int)GetDouble(panel, "minWidth", tokens.PanelMinWidth);
                tokens.PanelMaxWidth = (int)GetDouble(panel, "maxWidth", tokens.PanelMaxWidth);
                tokens.PanelDefaultHeight = (int)GetDouble(panel, "defaultHeight", tokens.PanelDefaultHeight);
            }

            if (root.TryGetProperty("color", out var color) &&
                color.TryGetProperty("agentBadge", out var badges))
            {
                foreach (var prop in badges.EnumerateObject())
                {
                    tokens._agentColors[prop.Name] = ParseColor(prop.Value.GetString() ?? "#808080");
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
