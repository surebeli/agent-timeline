using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgentTimeline.Core;
using Microsoft.UI.Xaml.Media;

namespace AgentTimeline.UI;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>
/// One codename chip on a card: the name plus a live status badge (✓ 完成 / △ 变更 / ▶ 进行中,
/// mirroring the mac CodenameChip). Badge state is refreshed from the registry whenever the
/// coordinator reports dictionary changes.
/// </summary>
public sealed class CodenameChipViewModel : ObservableObject
{
    private string _statusGlyph = "";
    private SolidColorBrush _statusBrush = new(Microsoft.UI.Colors.Transparent);

    public CodenameChipViewModel(string name) => Name = name;

    public string Name { get; }

    public string StatusGlyph
    {
        get => _statusGlyph;
        private set { if (Set(ref _statusGlyph, value)) Raise(nameof(HasStatus)); }
    }
    public bool HasStatus => _statusGlyph.Length > 0;

    public SolidColorBrush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }

    public void Refresh(CodenameEntry? entry, DesignTokens tokens)
    {
        var status = entry?.StatusValue;
        StatusGlyph = DesignTokens.StatusGlyph(status);
        if (HasStatus) StatusBrush = new SolidColorBrush(tokens.StatusColor(status));
    }
}

/// <summary>One timeline card. All members are read/written on the UI thread only.</summary>
public sealed class NodeViewModel : ObservableObject
{
    private static readonly Dictionary<AgentKind, SolidColorBrush> BrushCache = new();

    private readonly DesignTokens _tokens;
    private readonly Func<string, CodenameEntry?> _codenameLookup;

    private string _title = "";
    private List<string> _keyPoints = new();
    private List<CodenameChipViewModel> _codenames = new();
    private string? _kind;
    private string? _resultLine;
    private bool _isExpanded;
    private bool _summaryPending;

    public NodeViewModel(TimelineNode node, DesignTokens tokens, Func<string, CodenameEntry?> codenameLookup)
    {
        _tokens = tokens;
        _codenameLookup = codenameLookup;
        Id = node.Id;
        Timestamp = node.Command.Timestamp;
        Project = node.Command.Project;
        AgentName = node.Command.Agent.DisplayName();
        AgentBrush = BrushFor(node.Command.Agent, tokens);
        PromptText = node.Command.Text;
        SessionId = node.Command.SessionId;
        _summaryPending = node.SummaryPending;
        ApplySummary(node.Summary);
    }

    public long Id { get; }
    public DateTimeOffset Timestamp { get; }
    public string Project { get; }
    public string AgentName { get; }
    public SolidColorBrush AgentBrush { get; }
    public string PromptText { get; }
    public string SessionId { get; }

    public string TimeText
    {
        get
        {
            var local = Timestamp.ToLocalTime();
            return local.Date == DateTimeOffset.Now.Date
                ? local.ToString("HH:mm")
                : local.ToString("M月d日 HH:mm");
        }
    }

    public string Title { get => _title; private set => Set(ref _title, value); }

    public List<string> KeyPoints
    {
        get => _keyPoints;
        private set { if (Set(ref _keyPoints, value)) Raise(nameof(HasKeyPoints)); }
    }
    public bool HasKeyPoints => _keyPoints.Count > 0;

    public List<CodenameChipViewModel> Codenames
    {
        get => _codenames;
        private set { if (Set(ref _codenames, value)) Raise(nameof(HasCodenames)); }
    }
    public bool HasCodenames => _codenames.Count > 0;

    // --- Node kind tag (PRD §3.3b): label colored from color.kind in the design tokens ---

    public string? Kind
    {
        get => _kind;
        private set
        {
            if (!Set(ref _kind, value)) return;
            Raise(nameof(HasKind));
            Raise(nameof(KindBrush));
            Raise(nameof(KindBgBrush));
        }
    }
    public bool HasKind => !string.IsNullOrEmpty(_kind);

    public SolidColorBrush KindBrush =>
        new(_tokens.KindColor(_kind) ?? Microsoft.UI.Colors.Gray);

    /// <summary>Same kind color at 14% alpha — the tag pill background (mac opacity 0.14).</summary>
    public SolidColorBrush KindBgBrush
    {
        get
        {
            var c = _tokens.KindColor(_kind) ?? Microsoft.UI.Colors.Gray;
            return new SolidColorBrush(Windows.UI.Color.FromArgb(0x24, c.R, c.G, c.B));
        }
    }

    public string? ResultLine
    {
        get => _resultLine;
        set { if (Set(ref _resultLine, value)) Raise(nameof(HasResultLine)); }
    }
    public bool HasResultLine => !string.IsNullOrWhiteSpace(_resultLine);

    public bool SummaryPending { get => _summaryPending; set => Set(ref _summaryPending, value); }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value)) Raise(nameof(ExpandGlyph)); }
    }

    /// <summary>Segoe Fluent Icons chevron: down (expand) / up (collapse).</summary>
    public string ExpandGlyph => _isExpanded ? "\uE70E" : "\uE70D";

    public void ApplySummary(Summary summary)
    {
        Title = summary.Title;
        KeyPoints = summary.KeyPoints.ToList();
        Kind = NodeKinds.Normalize(summary.Kind);
        if (summary.ResultLine is not null) ResultLine = summary.ResultLine;

        // Chips show the union of extracted codenames (LLM + rule definitions) and regex
        // candidates, consistent with what CodenameRegistry registered for this node.
        var union = new List<string>();
        foreach (var cn in summary.Codenames)
        {
            if (!union.Contains(cn.Name)) union.Add(cn.Name);
        }
        foreach (var name in CodenameRegistry.ExtractCandidates(PromptText))
        {
            if (!union.Contains(name)) union.Add(name);
        }
        Codenames = union.Select(name =>
        {
            var chip = new CodenameChipViewModel(name);
            chip.Refresh(_codenameLookup(name), _tokens);
            return chip;
        }).ToList();
    }

    /// <summary>Re-reads chip status badges from the registry (dictionary changed).</summary>
    public void RefreshCodenameStatuses()
    {
        foreach (var chip in _codenames)
        {
            chip.Refresh(_codenameLookup(chip.Name), _tokens);
        }
    }

    private static SolidColorBrush BrushFor(AgentKind kind, DesignTokens tokens)
    {
        if (!BrushCache.TryGetValue(kind, out var brush))
        {
            brush = new SolidColorBrush(tokens.AgentColor(kind));
            BrushCache[kind] = brush;
        }
        return brush;
    }
}

/// <summary>
/// The timeline (PRD F2): newest first, lazily paged, filterable by project and by node
/// kind (PRD §3.3b 阶段过滤). Fed by TimelineCoordinator events which MainWindow marshals
/// onto the UI thread.
/// </summary>
public sealed class TimelineViewModel : ObservableObject
{
    public const string AllProjects = "全部项目";
    public const string AllKinds = "全部阶段";
    private const int PageSize = 200;

    private readonly Store _store;
    private readonly CodenameRegistry _registry;
    private readonly DesignTokens _tokens;
    private readonly HashSet<long> _knownIds = new();
    private readonly Dictionary<long, NodeViewModel> _byId = new();
    private string? _projectFilter; // null = all
    private string? _kindFilter;    // NodeKind label; null = all
    private bool _hasMore;

    public TimelineViewModel(Store store, CodenameRegistry registry, DesignTokens tokens)
    {
        _store = store;
        _registry = registry;
        _tokens = tokens;
        foreach (var label in NodeKinds.AllLabels) KindOptions.Add(label);
    }

    public ObservableCollection<NodeViewModel> Nodes { get; } = new();
    public ObservableCollection<string> ProjectOptions { get; } = new() { AllProjects };
    public ObservableCollection<string> KindOptions { get; } = new() { AllKinds };

    public bool HasMore { get => _hasMore; private set => Set(ref _hasMore, value); }

    public void LoadInitial()
    {
        foreach (var project in _store.GetProjects()) EnsureProjectOption(project);

        Nodes.Clear();
        _knownIds.Clear();
        _byId.Clear();
        var page = _store.GetRecentNodes(PageSize, long.MaxValue, _projectFilter, _kindFilter);
        foreach (var node in page) Append(node);
        HasMore = page.Count == PageSize;
    }

    public void LoadMore()
    {
        var cursor = Nodes.Count > 0 ? Nodes[^1].Id : long.MaxValue;
        var page = _store.GetRecentNodes(PageSize, cursor, _projectFilter, _kindFilter);
        foreach (var node in page) Append(node);
        HasMore = page.Count == PageSize;
    }

    public void SetProjectFilter(string option)
    {
        _projectFilter = option == AllProjects ? null : option;
        LoadInitial();
    }

    public void SetKindFilter(string option)
    {
        _kindFilter = option == AllKinds ? null : option;
        LoadInitial();
    }

    public bool HasActiveFilters => _projectFilter is not null || _kindFilter is not null;

    /// <summary>Drops both filters and reloads (used before jumping to a filtered-out node).</summary>
    public void ClearFilters()
    {
        _projectFilter = null;
        _kindFilter = null;
        LoadInitial();
    }

    /// <summary>
    /// Pages until <paramref name="nodeId"/> is materialized (jump target beyond the loaded
    /// window, e.g. an old definition node). Bounded so a huge history cannot stall the UI.
    /// </summary>
    public bool EnsureLoaded(long nodeId)
    {
        var guard = 0;
        while (!_byId.ContainsKey(nodeId) && HasMore && guard++ < 50)
        {
            LoadMore();
        }
        return _byId.ContainsKey(nodeId);
    }

    /// <summary>Chip badge refresh after the codename dictionary changed.</summary>
    public void RefreshCodenameStatuses()
    {
        foreach (var vm in _byId.Values) vm.RefreshCodenameStatuses();
    }

    // ------------------------------------------------- coordinator event sinks (UI thread)

    public void OnNodeAdded(TimelineNode node)
    {
        EnsureProjectOption(node.Command.Project);
        if (_projectFilter is not null && node.Command.Project != _projectFilter) return;
        if (_kindFilter is not null && node.Summary.Kind != _kindFilter) return;
        if (!_knownIds.Add(node.Id)) return;

        var vm = new NodeViewModel(node, _tokens, _registry.Lookup);
        // Newest on top; backfill may deliver out of order, so find the insertion point.
        var index = 0;
        while (index < Nodes.Count && Nodes[index].Timestamp > vm.Timestamp) index++;
        Nodes.Insert(index, vm);
        _byId[node.Id] = vm;
    }

    public void OnSummaryUpdated(long nodeId, Summary summary)
    {
        if (!_byId.TryGetValue(nodeId, out var vm)) return;
        vm.ApplySummary(summary);
        vm.SummaryPending = false;
    }

    public void OnResultLineUpdated(long nodeId, string resultLine)
    {
        if (_byId.TryGetValue(nodeId, out var vm)) vm.ResultLine = resultLine;
    }

    public NodeViewModel? FindById(long nodeId) =>
        _byId.TryGetValue(nodeId, out var vm) ? vm : null;

    public int IndexOf(NodeViewModel vm) => Nodes.IndexOf(vm);

    private void Append(TimelineNode node)
    {
        if (!_knownIds.Add(node.Id)) return;
        var vm = new NodeViewModel(node, _tokens, _registry.Lookup);
        Nodes.Add(vm);
        _byId[node.Id] = vm;
    }

    private void EnsureProjectOption(string project)
    {
        if (string.IsNullOrWhiteSpace(project) || ProjectOptions.Contains(project)) return;
        ProjectOptions.Add(project);
    }
}
