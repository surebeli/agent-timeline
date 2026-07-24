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

/// <summary>One timeline card. All members are read/written on the UI thread only.</summary>
public sealed class NodeViewModel : ObservableObject
{
    private static readonly Dictionary<AgentKind, SolidColorBrush> BrushCache = new();

    private string _title = "";
    private List<string> _keyPoints = new();
    private List<string> _codenames = new();
    private string? _resultLine;
    private bool _isExpanded;
    private bool _summaryPending;

    public NodeViewModel(TimelineNode node, DesignTokens tokens)
    {
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

    public List<string> Codenames
    {
        get => _codenames;
        private set { if (Set(ref _codenames, value)) Raise(nameof(HasCodenames)); }
    }
    public bool HasCodenames => _codenames.Count > 0;

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
        if (summary.ResultLine is not null) ResultLine = summary.ResultLine;

        // Chips show the union of LLM-extracted codenames and regex candidates,
        // consistent with what CodenameRegistry registered for this node.
        var union = new List<string>();
        foreach (var cn in summary.Codenames)
        {
            if (!union.Contains(cn.Name)) union.Add(cn.Name);
        }
        foreach (var name in CodenameRegistry.ExtractCandidates(PromptText))
        {
            if (!union.Contains(name)) union.Add(name);
        }
        Codenames = union;
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
/// The timeline (PRD F2): newest first, lazily paged, filterable by project.
/// Fed by TimelineCoordinator events which MainWindow marshals onto the UI thread.
/// </summary>
public sealed class TimelineViewModel : ObservableObject
{
    public const string AllProjects = "全部项目";
    private const int PageSize = 200;

    private readonly Store _store;
    private readonly DesignTokens _tokens;
    private readonly HashSet<long> _knownIds = new();
    private readonly Dictionary<long, NodeViewModel> _byId = new();
    private string? _projectFilter; // null = all
    private bool _hasMore;

    public TimelineViewModel(Store store, DesignTokens tokens)
    {
        _store = store;
        _tokens = tokens;
    }

    public ObservableCollection<NodeViewModel> Nodes { get; } = new();
    public ObservableCollection<string> ProjectOptions { get; } = new() { AllProjects };

    public bool HasMore { get => _hasMore; private set => Set(ref _hasMore, value); }

    public void LoadInitial()
    {
        foreach (var project in _store.GetProjects()) EnsureProjectOption(project);

        Nodes.Clear();
        _knownIds.Clear();
        _byId.Clear();
        var page = _store.GetRecentNodes(PageSize, long.MaxValue, _projectFilter);
        foreach (var node in page) Append(node);
        HasMore = page.Count == PageSize;
    }

    public void LoadMore()
    {
        var cursor = Nodes.Count > 0 ? Nodes[^1].Id : long.MaxValue;
        var page = _store.GetRecentNodes(PageSize, cursor, _projectFilter);
        foreach (var node in page) Append(node);
        HasMore = page.Count == PageSize;
    }

    public void SetProjectFilter(string option)
    {
        _projectFilter = option == AllProjects ? null : option;
        LoadInitial();
    }

    // ------------------------------------------------- coordinator event sinks (UI thread)

    public void OnNodeAdded(TimelineNode node)
    {
        EnsureProjectOption(node.Command.Project);
        if (_projectFilter is not null && node.Command.Project != _projectFilter) return;
        if (!_knownIds.Add(node.Id)) return;

        var vm = new NodeViewModel(node, _tokens);
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
        var vm = new NodeViewModel(node, _tokens);
        Nodes.Add(vm);
        _byId[node.Id] = vm;
    }

    private void EnsureProjectOption(string project)
    {
        if (string.IsNullOrWhiteSpace(project) || ProjectOptions.Contains(project)) return;
        ProjectOptions.Add(project);
    }
}
