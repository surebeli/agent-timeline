using System.Text.RegularExpressions;

namespace AgentTimeline.Core;

/// <summary>
/// The codename dictionary (PRD F3): union of regex candidates and LLM-extracted codenames.
/// First occurrence defines the entry (first-seen wins); later occurrences only bump the
/// counter and may fill in a missing definition from a later LLM summary.
/// Backed by the SQLite `codenames` table, fronted by an in-memory cache for chip lookups.
/// </summary>
public sealed partial class CodenameRegistry
{
    /// <summary>
    /// Candidate pattern, e.g. T-PLUGIN-00, HOP-CLI-7, FOO-BAR-BAZ (2–4 dash-separated
    /// uppercase/digit groups, first group starts with a letter).
    /// NOTE: the first quantifier is {0,9} (not {1,9}) so single-letter first segments
    /// like "T-PLUGIN-00" match — the PRD's own flagship example requires this
    /// (verified by the Core smoke test).
    /// </summary>
    public static readonly Regex CandidateRegex = new(
        @"\b[A-Z][A-Z0-9]{0,9}(?:-[A-Z0-9]{1,10}){1,3}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Store _store;
    private readonly Dictionary<string, CodenameEntry> _cache = new();
    private readonly object _gate = new();

    public CodenameRegistry(Store store)
    {
        _store = store;
        foreach (var entry in store.GetAllCodenames())
        {
            _cache[entry.Name] = entry;
        }
    }

    public static IReadOnlyList<string> ExtractCandidates(string text)
    {
        var seen = new List<string>();
        foreach (Match m in CandidateRegex.Matches(text))
        {
            if (!seen.Contains(m.Value)) seen.Add(m.Value);
        }
        return seen;
    }

    public CodenameEntry? Lookup(string name)
    {
        lock (_gate)
        {
            return _cache.TryGetValue(name, out var entry) ? entry : null;
        }
    }

    public IReadOnlyList<CodenameEntry> All()
    {
        lock (_gate)
        {
            return _cache.Values.OrderByDescending(e => e.FirstSeen).ToList();
        }
    }

    /// <summary>
    /// Registers all codenames appearing in one command (regex candidates ∪ LLM extraction).
    /// <paramref name="countOccurrences"/> is false when re-registering the SAME node after its
    /// LLM summary arrives — definitions get filled in, but counters must not double-count.
    /// Returns the union list (used to render chips).
    /// </summary>
    public IReadOnlyList<string> RegisterOccurrences(
        long nodeId,
        DateTimeOffset timestamp,
        string text,
        IReadOnlyList<CodenameDefinition> llmCodenames,
        bool countOccurrences = true)
    {
        var union = new List<string>(ExtractCandidates(text));
        var definitions = new Dictionary<string, string?>();
        foreach (var cn in llmCodenames)
        {
            if (string.IsNullOrWhiteSpace(cn.Name)) continue;
            if (!union.Contains(cn.Name)) union.Add(cn.Name);
            definitions[cn.Name] = cn.Definition;
        }

        lock (_gate)
        {
            foreach (var name in union)
            {
                definitions.TryGetValue(name, out var llmDefinition);
                if (_cache.TryGetValue(name, out var existing))
                {
                    if (countOccurrences && existing.DefiningNodeId != nodeId) existing.Occurrences++;
                    if (existing.Definition is null && llmDefinition is not null)
                    {
                        existing.Definition = llmDefinition;
                    }
                    _store.UpsertCodename(existing);
                }
                else
                {
                    var entry = new CodenameEntry
                    {
                        Name = name,
                        FirstSeen = timestamp,
                        DefiningNodeId = nodeId,
                        Definition = llmDefinition,
                        ContextExcerpt = ExcerptAround(text, name),
                        Occurrences = 1,
                    };
                    _cache[name] = entry;
                    _store.UpsertCodename(entry);
                }
            }
        }
        return union;
    }

    /// <summary>±40 chars of context around the first occurrence, single line.</summary>
    private static string ExcerptAround(string text, string name)
    {
        var idx = text.IndexOf(name, StringComparison.Ordinal);
        if (idx < 0) return "";
        var start = Math.Max(0, idx - 40);
        var end = Math.Min(text.Length, idx + name.Length + 40);
        var excerpt = text[start..end].ReplaceLineEndings(" ").Trim();
        return (start > 0 ? "…" : "") + excerpt + (end < text.Length ? "…" : "");
    }
}
