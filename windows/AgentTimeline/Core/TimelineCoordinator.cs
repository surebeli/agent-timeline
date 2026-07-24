using AgentTimeline.Core.Parsers;
using AgentTimeline.Core.Summarize;

namespace AgentTimeline.Core;

/// <summary>
/// Wires the pipeline described in docs/ARCHITECTURE.md 数据流:
///
///   FileSystemWatcher → SessionWatcher → Parser → SessionEvent
///                                            │
///                     UserCommand / TaskComplete
///                                            ▼
///                                     Store (SQLite, single write point)
///                                            │ un-summarized commands
///                                            ▼
///                                     SummaryEngine ── cache ──> Store
///                                            │
///                                     events → TimelineViewModel (via DispatcherQueue)
///
/// Nodes hit the screen immediately with the RuleSummarizer digest; when the LLM summary
/// arrives the node is refreshed in place.
/// All events here fire on background threads — the UI layer marshals to its dispatcher.
/// </summary>
public sealed class TimelineCoordinator : IDisposable
{
    private readonly Store _store;
    private readonly CodenameRegistry _registry;
    private readonly SummaryEngine _engine;
    private readonly SessionWatcher _watcher;
    private readonly RuleSummarizer _rule = new();

    public event Action<TimelineNode>? NodeAdded;
    public event Action<long, Summary>? NodeSummaryUpdated;
    public event Action<long, string>? NodeResultLineUpdated;

    public TimelineCoordinator(Store store, CodenameRegistry registry, SummaryEngine engine, AppSettings settings)
    {
        _store = store;
        _registry = registry;
        _engine = engine;

        var parsers = new List<IAgentSessionParser>
        {
            new ClaudeParser(),
            new CodexParser(),
            new KimiParser(),
            new ZcodeParser(settings), // reserved adapter, returns no events until implemented
        };
        _watcher = new SessionWatcher(store, settings, parsers);
        _watcher.EventsParsed += OnEventsParsed;

        _engine.Summarized += OnLlmSummarized;
        _engine.SummaryFailed += OnLlmFailed;
    }

    public void Start() => _watcher.Start();

    private void OnEventsParsed(IReadOnlyList<SessionEvent> events)
    {
        foreach (var evt in events)
        {
            try
            {
                switch (evt)
                {
                    case UserCommand cmd:
                        IngestUserCommand(cmd);
                        break;
                    case TaskComplete done:
                        if (_store.SetResultLine(done.Agent, done.SessionId, done.ResultLine) is { } nodeId)
                        {
                            NodeResultLineUpdated?.Invoke(nodeId, done.ResultLine);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Coordinator failed to ingest event", ex);
            }
        }
    }

    private void IngestUserCommand(UserCommand cmd)
    {
        var hash = SummaryEngine.ComputeHash(cmd);

        // Cache hit (same command seen before) → final summary immediately, no LLM call.
        var cached = _store.GetCachedSummary(hash);
        var summary = cached ?? _rule.Summarize(cmd);
        var pending = cached is null && _engine.HasLlm;

        var id = _store.InsertNode(cmd, summary, hash, pending);
        if (id < 0) return; // duplicate (already ingested in an earlier run)

        _registry.RegisterOccurrences(id, cmd.Timestamp, cmd.Text, summary.Codenames);

        var node = new TimelineNode
        {
            Id = id,
            Command = cmd,
            Summary = summary,
            CommandHash = hash,
            SummaryPending = pending,
        };
        NodeAdded?.Invoke(node);

        if (pending) _engine.Enqueue(id, cmd, hash);
    }

    private void OnLlmSummarized(long nodeId, string hash, Summary summary)
    {
        _store.UpdateSummary(nodeId, summary, pending: false);
        _store.CacheSummary(hash, summary);

        // Re-register only to pick up LLM definitions; counters must not double-count.
        var node = _store.GetNode(nodeId);
        if (node is not null)
        {
            _registry.RegisterOccurrences(
                nodeId, node.Command.Timestamp, node.Command.Text,
                summary.Codenames, countOccurrences: false);
        }
        NodeSummaryUpdated?.Invoke(nodeId, summary);
    }

    private void OnLlmFailed(long nodeId)
    {
        // Keep the rule summary on screen; summary_pending stays 1 → retried on next launch.
        Log.Warn($"LLM summary failed for node {nodeId}; keeping rule summary (pending retry)");
    }

    /// <summary>Re-enqueues nodes whose LLM summary never landed (called once at startup).</summary>
    public void RetryPendingSummaries()
    {
        if (!_engine.HasLlm) return;
        foreach (var node in _store.GetRecentNodes(limit: 100).Where(n => n.SummaryPending))
        {
            _engine.Enqueue(node.Id, node.Command, node.CommandHash);
        }
    }

    public void Dispose()
    {
        _watcher.EventsParsed -= OnEventsParsed;
        _watcher.Dispose();
    }
}
