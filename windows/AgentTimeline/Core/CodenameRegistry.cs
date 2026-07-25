namespace AgentTimeline.Core;

/// <summary>
/// The codename dictionary (PRD F3 + 生命周期扩展) — mirrors macos CodenameRegistry.
/// Merges three sources: rule-based text mining of user commands AND agent replies,
/// plus LLM extraction. Persistence semantics live in the Store (DefineCodename
/// latest-wins with 变更 flip / RecordCodename fill-empty / TouchCodename status
/// machine); this class orchestrates and fronts an in-memory cache for UI lookups.
/// </summary>
public sealed class CodenameRegistry
{
    private readonly Store _store;
    private readonly Dictionary<string, CodenameEntry> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public CodenameRegistry(Store store)
    {
        _store = store;
        ReloadCache();
    }

    /// <summary>Dash-style regex candidates in a text (used by the UI to render chips).</summary>
    public static IReadOnlyList<string> ExtractCandidates(string text) => CodenameDetector.Detect(text);

    public CodenameEntry? Lookup(string name)
    {
        lock (_gate)
        {
            return _cache.TryGetValue(name, out var entry) ? entry : null;
        }
    }

    /// <summary>Dictionary panel ordering: recently updated first, then recently seen.</summary>
    public IReadOnlyList<CodenameEntry> All()
    {
        lock (_gate)
        {
            return _cache.Values
                .OrderByDescending(e => e.Updated ?? e.FirstSeen)
                .ToList();
        }
    }

    /// <summary>
    /// Mine one text (user command or agent reply): definitions, dash-style mentions,
    /// then status updates against everything known (including codes just defined).
    /// Returns true when any dictionary row was touched (UI refresh signal).
    /// </summary>
    public bool ProcessText(long nodeId, DateTimeOffset at, string text)
    {
        HashSet<string> known;
        lock (_gate)
        {
            known = new HashSet<string>(_cache.Keys, StringComparer.Ordinal);
        }
        var touched = new HashSet<string>(StringComparer.Ordinal);
        var definedNow = new HashSet<string>(StringComparer.Ordinal);
        var bornNow = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, definition) in CodenameDetector.DetectDefinitions(text))
        {
            _store.DefineCodename(name, definition, nodeId, at);
            definedNow.Add(name);
            known.Add(name);
            touched.Add(name);
        }
        foreach (var name in CodenameDetector.Detect(text))
        {
            if (known.Contains(name)) continue;
            _store.RecordCodename(name, "", nodeId, at);
            known.Add(name);
            bornNow.Add(name);
            touched.Add(name);
        }
        // A definition sentence is not a status update about itself — keywords in the
        // definition body ("N1: 完成支付重构") must not flip the fresh 定义 status, and
        // DefineCodename already counted the occurrence.
        var mentionTargets = new HashSet<string>(known, StringComparer.Ordinal);
        mentionTargets.ExceptWith(definedNow);
        foreach (var (name, status, context) in CodenameDetector.DetectMentions(text, mentionTargets))
        {
            _store.TouchCodename(name, status, context, nodeId, at,
                bumpOccurrence: !bornNow.Contains(name));
            touched.Add(name);
        }

        RefreshFromStore(touched);
        return touched.Count > 0;
    }

    /// <summary>
    /// Register LLM-extracted codenames: soft sighting (never overwrites an existing
    /// definition), plus a status-machine advance when the model saw a lifecycle signal
    /// (定义/提及 carry no transition and are skipped, mirroring mac). Names must pass
    /// the plausibility gate — models occasionally emit list indices as "codenames".
    /// </summary>
    public bool RecordFromSummary(Summary summary, long nodeId, DateTimeOffset seenAt)
    {
        var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in summary.Codenames)
        {
            var name = def.Name?.Trim();
            if (name is null || !CodenameDetector.IsPlausibleName(name)) continue;
            _store.RecordCodename(name, def.Definition ?? "", nodeId, seenAt);
            touched.Add(name);
            if (CodenameStatuses.FromLabel(def.Status) is { } status &&
                status != CodenameStatus.Defined && status != CodenameStatus.Mentioned)
            {
                _store.TouchCodename(name, status, "", nodeId, seenAt);
            }
        }
        RefreshFromStore(touched);
        return touched.Count > 0;
    }

    /// <summary>Rebuilds the whole cache from the store (used after the one-time replay).</summary>
    public void ReloadCache()
    {
        var all = _store.GetAllCodenames();
        lock (_gate)
        {
            _cache.Clear();
            foreach (var entry in all)
            {
                _cache[entry.Name] = entry;
            }
        }
    }

    private void RefreshFromStore(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var entry = _store.GetCodename(name);
            if (entry is null) continue;
            lock (_gate)
            {
                _cache[name] = entry;
            }
        }
    }
}
