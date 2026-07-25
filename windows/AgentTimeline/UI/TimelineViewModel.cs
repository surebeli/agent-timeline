using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgentTimeline.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

/// <summary>Pinned-style day divider row (今天 · n条 / 昨天 / MM-dd · ddd).</summary>
public sealed class DayHeaderViewModel
{
    public DayHeaderViewModel(string label, DateTime day)
    {
        Label = label;
        Day = day;
    }

    public string Label { get; }
    public DateTime Day { get; }
}

/// <summary>Picks the day-header vs ledger-entry template for the mixed timeline list.</summary>
public sealed class TimelineItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? NodeTemplate { get; set; }
    public DataTemplate? DayHeaderTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is DayHeaderViewModel ? DayHeaderTemplate : NodeTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}

/// <summary>
/// One ledger entry (无框"双墨线台账", PRD §3.2b). All members are read/written on the UI
/// thread only. The visual grammar: ❯ + solid agent rule + opaque paper block = my literal
/// words (always visible, 3-line clamp collapsed); ✦ + dotted gray rule = machine-derived.
/// </summary>
public sealed class NodeViewModel : ObservableObject
{
    private static readonly Dictionary<AgentKind, SolidColorBrush> BrushCache = new();

    private readonly DesignTokens _tokens;
    private readonly Func<string, CodenameEntry?> _codenameLookup;

    private string _title = "";
    private string? _derivedTitle;
    private List<string> _keyPoints = new();
    private List<CodenameChipViewModel> _codenames = new();
    private string? _kind;
    private string? _resultLine;
    private bool _isExpanded;
    private bool _summaryPending;
    private bool _isHovering;
    private bool _isCopied;
    private bool _isDefinitionNode;

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

    public string TimeText => Timestamp.ToLocalTime().ToString("MM-dd HH:mm");

    public string Title { get => _title; private set => Set(ref _title, value); }

    // --- Derived block (✦ + dotted rule): title, keypoints digest, chips, result line ---

    /// <summary>LLM title, suppressed when it merely echoes a short command.</summary>
    public string? DerivedTitle
    {
        get => _derivedTitle;
        private set { if (Set(ref _derivedTitle, value)) Raise(nameof(HasDerivedTitle)); }
    }
    public bool HasDerivedTitle => !string.IsNullOrEmpty(_derivedTitle);

    public List<string> KeyPoints
    {
        get => _keyPoints;
        private set
        {
            if (!Set(ref _keyPoints, value)) return;
            Raise(nameof(HasKeyPoints));
            Raise(nameof(KeypointsDigest));
            Raise(nameof(KeypointsOverflow));
            Raise(nameof(HasKeypointsOverflow));
            Raise(nameof(ShowKeypointsDigest));
            Raise(nameof(ShowKeypointsList));
        }
    }
    public bool HasKeyPoints => _keyPoints.Count > 0;

    /// <summary>Collapsed one-line digest: points joined with " · ".</summary>
    public string KeypointsDigest => string.Join(" · ", _keyPoints);
    /// <summary>Accent "+n" counter when more than 2 points hide behind the digest.</summary>
    public string KeypointsOverflow => _keyPoints.Count > 2 ? $"+{_keyPoints.Count - 2}" : "";
    public bool HasKeypointsOverflow => _keyPoints.Count > 2;
    public bool ShowKeypointsDigest => HasKeyPoints && !_isExpanded;
    public bool ShowKeypointsList => HasKeyPoints && _isExpanded;

    public List<CodenameChipViewModel> Codenames
    {
        get => _codenames;
        private set
        {
            if (!Set(ref _codenames, value)) return;
            Raise(nameof(HasCodenames));
            Raise(nameof(HasDerived));
        }
    }
    public bool HasCodenames => _codenames.Count > 0;

    /// <summary>Whether anything machine-derived exists (the whole ✦ block hides otherwise).</summary>
    public bool HasDerived => HasDerivedTitle || HasKeyPoints || HasCodenames || HasResultLine;

    public string? FirstChipName => _codenames.Count > 0 ? _codenames[0].Name : null;

    /// <summary>右键 "复制摘要": title + keypoints + result line.</summary>
    public string SummaryClipboardText
    {
        get
        {
            var parts = new List<string>();
            if (_title.Length > 0) parts.Add(_title);
            parts.AddRange(_keyPoints);
            if (!string.IsNullOrEmpty(_resultLine)) parts.Add("→ " + _resultLine);
            return string.Join("\n", parts);
        }
    }

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
            Raise(nameof(MarkerBrush));
            Raise(nameof(ShowAnchorMarker));
            Raise(nameof(ShowStandardMarker));
            Raise(nameof(ShowMinorMarker));
            Raise(nameof(HasAnchorWash));
            Raise(nameof(AnchorWashBrush));
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

    // --- Rail marker + anchor wash (ledger grammar, PRD \u00A73.2b) ---

    private bool IsAnchorKind =>
        _kind == NodeKind.Requirement.Label() || _kind == NodeKind.Decision.Label();
    private bool IsStandardKind =>
        _kind == NodeKind.Task.Label() || _kind == NodeKind.Fix.Label()
        || _kind == NodeKind.Research.Label() || _kind == NodeKind.Learning.Label();

    /// <summary>Rail marker: \u9700\u6C42/\u51B3\u7B56 diamond, \u4EFB\u52A1/\u4FEE\u590D/\u8C03\u7814/\u5B66\u4E60 dot, else hollow circle.</summary>
    public bool ShowAnchorMarker => IsAnchorKind;
    public bool ShowStandardMarker => IsStandardKind;
    public bool ShowMinorMarker => !IsAnchorKind && !IsStandardKind;

    public SolidColorBrush MarkerBrush => KindBrush;

    /// <summary>\u9700\u6C42/\u51B3\u7B56 anchors get a faint kind-colored wash across the whole entry.</summary>
    public bool HasAnchorWash => IsAnchorKind;

    public SolidColorBrush AnchorWashBrush
    {
        get
        {
            var c = _tokens.KindColor(_kind) ?? Microsoft.UI.Colors.Transparent;
            var alpha = (byte)Math.Clamp(Math.Round(_tokens.AnchorWashOpacity * 255), 0, 255);
            return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, c.R, c.G, c.B));
        }
    }

    /// <summary>Codename-defining nodes get an accent ring on their rail marker.</summary>
    public bool IsDefinitionNode
    {
        get => _isDefinitionNode;
        set => Set(ref _isDefinitionNode, value);
    }

    public string? ResultLine
    {
        get => _resultLine;
        set
        {
            if (!Set(ref _resultLine, value)) return;
            Raise(nameof(HasResultLine));
            Raise(nameof(HasDerived));
            Raise(nameof(SummaryClipboardText));
        }
    }
    public bool HasResultLine => !string.IsNullOrWhiteSpace(_resultLine);

    public bool SummaryPending { get => _summaryPending; set => Set(ref _summaryPending, value); }

    // --- Expand / hover / copy interaction state ---

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!Set(ref _isExpanded, value)) return;
            Raise(nameof(ChevronAngle));
            Raise(nameof(CommandMaxLines));
            Raise(nameof(ResultLineMaxLines));
            Raise(nameof(ShowKeypointsDigest));
            Raise(nameof(ShowKeypointsList));
        }
    }

    /// <summary>Chevron rotates 180\u00B0 when expanded (bound to its RotateTransform).</summary>
    public double ChevronAngle => _isExpanded ? 180.0 : 0.0;

    /// <summary>Raw command hero: 3-line clamp collapsed, unlimited (0) expanded.</summary>
    public int CommandMaxLines => _isExpanded ? 0 : _tokens.CommandCollapsedLines;

    public int ResultLineMaxLines => _isExpanded ? 0 : 1;

    /// <summary>Pointer-over state; shows the entryHover layer and the copy button.</summary>
    public bool IsHovering
    {
        get => _isHovering;
        set { if (Set(ref _isHovering, value)) Raise(nameof(ShowCopyButton)); }
    }

    /// <summary>Copy receipt: glyph morphs to a checkmark in resultLine green for 800ms.</summary>
    public bool IsCopied
    {
        get => _isCopied;
        set
        {
            if (!Set(ref _isCopied, value)) return;
            Raise(nameof(CopyGlyph));
            Raise(nameof(CopyBrush));
            Raise(nameof(ShowCopyButton));
        }
    }

    public bool ShowCopyButton => _isHovering || _isCopied;
    /// <summary>Segoe Fluent Icons: Copy \u2192 Accept(checkmark) while the receipt shows.</summary>
    public string CopyGlyph => _isCopied ? "\uE73E" : "\uE8C8";
    public SolidColorBrush CopyBrush => new(_isCopied
        ? _tokens.DualColor("resultLine")
        : _tokens.DualColor("textTertiary"));

    public void ApplySummary(Summary summary)
    {
        Title = summary.Title;
        KeyPoints = summary.KeyPoints.ToList();
        Kind = NodeKinds.Normalize(summary.Kind);
        DerivedTitle = ComputeDerivedTitle(summary.Title);
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
        Raise(nameof(HasDerived));
        Raise(nameof(SummaryClipboardText));
        Raise(nameof(FirstChipName));
    }

    /// <summary>
    /// Suppress the derived title when the command is short (≤20 chars) or the title is a
    /// normalized prefix-duplicate of the command (mirrors mac NodeCardView.derivedTitle).
    /// </summary>
    private string? ComputeDerivedTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        if (PromptText.Length <= 20) return null;
        var normTitle = Normalize(title);
        var normCommand = Normalize(PromptText);
        if (normTitle.Length > 0 && normCommand.StartsWith(normTitle, StringComparison.Ordinal))
        {
            return null;
        }
        return title;

        static string Normalize(string s) =>
            new(s.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray());
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

    private readonly List<NodeViewModel> _ordered = new(); // newest first

    /// <summary>
    /// The ledger list: DayHeaderViewModel dividers interleaved with NodeViewModel entries
    /// (mac dayGroups + pinned sections; rendered via TimelineItemTemplateSelector).
    /// </summary>
    public ObservableCollection<object> Items { get; } = new();
    public ObservableCollection<string> ProjectOptions { get; } = new() { AllProjects };
    public ObservableCollection<string> KindOptions { get; } = new() { AllKinds };

    public bool HasMore { get => _hasMore; private set => Set(ref _hasMore, value); }

    public void LoadInitial()
    {
        foreach (var project in _store.GetProjects()) EnsureProjectOption(project);

        _ordered.Clear();
        _knownIds.Clear();
        _byId.Clear();
        var page = _store.GetRecentNodes(PageSize, long.MaxValue, _projectFilter, _kindFilter);
        foreach (var node in page) Append(node);
        HasMore = page.Count == PageSize;
        RebuildItems();
        RefreshDefinitionNodes();
    }

    public void LoadMore()
    {
        var cursor = _ordered.Count > 0 ? _ordered[^1].Id : long.MaxValue;
        var page = _store.GetRecentNodes(PageSize, cursor, _projectFilter, _kindFilter);
        foreach (var node in page) Append(node);
        HasMore = page.Count == PageSize;
        RebuildItems();
        RefreshDefinitionNodes();
    }

    /// <summary>
    /// Regenerates Items from the ordered node list: one day header per calendar day
    /// (今天 · n条 / 昨天 / MM-dd · ddd), then that day's entries. Wholesale rebuild keeps
    /// grouping trivially correct; entry state lives on the reused NodeViewModels.
    /// </summary>
    private void RebuildItems()
    {
        var counts = new Dictionary<DateTime, int>();
        foreach (var vm in _ordered)
        {
            var day = vm.Timestamp.ToLocalTime().Date;
            counts[day] = counts.GetValueOrDefault(day) + 1;
        }

        Items.Clear();
        DateTime? currentDay = null;
        foreach (var vm in _ordered)
        {
            var day = vm.Timestamp.ToLocalTime().Date;
            if (day != currentDay)
            {
                Items.Add(new DayHeaderViewModel(DayLabel(day, counts.GetValueOrDefault(day)), day));
                currentDay = day;
            }
            Items.Add(vm);
        }
    }

    private static string DayLabel(DateTime day, int count)
    {
        var today = DateTime.Now.Date;
        if (day == today) return $"今天 · {count}条";
        if (day == today.AddDays(-1)) return "昨天";
        return $"{day:MM-dd} · {day:ddd}";
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

    /// <summary>Chip badge + definition-ring refresh after the codename dictionary changed.</summary>
    public void RefreshCodenameStatuses()
    {
        foreach (var vm in _byId.Values) vm.RefreshCodenameStatuses();
        RefreshDefinitionNodes();
    }

    /// <summary>Nodes that first defined a codename get the accent ring on their rail marker.</summary>
    private void RefreshDefinitionNodes()
    {
        var ids = new HashSet<long>(
            _registry.All().Select(e => e.DefiningNodeId).Where(id => id > 0));
        foreach (var vm in _byId.Values)
        {
            vm.IsDefinitionNode = ids.Contains(vm.Id);
        }
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
        while (index < _ordered.Count && _ordered[index].Timestamp > vm.Timestamp) index++;
        _ordered.Insert(index, vm);
        _byId[node.Id] = vm;
        RebuildItems();
        RefreshDefinitionNodes();
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

    public int IndexOf(NodeViewModel vm) => Items.IndexOf(vm);

    private void Append(TimelineNode node)
    {
        if (!_knownIds.Add(node.Id)) return;
        var vm = new NodeViewModel(node, _tokens, _registry.Lookup);
        _ordered.Add(vm);
        _byId[node.Id] = vm;
    }

    private void EnsureProjectOption(string project)
    {
        if (string.IsNullOrWhiteSpace(project) || ProjectOptions.Contains(project)) return;
        ProjectOptions.Add(project);
    }
}
