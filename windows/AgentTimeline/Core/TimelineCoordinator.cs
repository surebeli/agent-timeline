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
    private readonly AppSettings _settings;
    private readonly SessionWatcher _watcher;
    private readonly RuleSummarizer _rule = new();

    public event Action<TimelineNode>? NodeAdded;
    public event Action<long, Summary>? NodeSummaryUpdated;
    public event Action<long, string>? NodeResultLineUpdated;
    /// <summary>Codename dictionary rows changed (statuses/definitions); UI refreshes chip badges.</summary>
    public event Action? CodenamesChanged;

    public TimelineCoordinator(Store store, CodenameRegistry registry, SummaryEngine engine, AppSettings settings)
    {
        _store = store;
        _registry = registry;
        _engine = engine;
        _settings = settings;

        var parsers = new List<IAgentSessionParser>
        {
            new ClaudeParser(),
            new CodexParser(),
            new KimiParser(),
            new ZcodeParser(), // ~\.zcode\cli\agents\**\transcript.jsonl（实机样例逆向实现）
        };
        _watcher = new SessionWatcher(store, settings, parsers);
        _watcher.EventsParsed += OnEventsParsed;

        _engine.Summarized += OnLlmSummarized;
        _engine.SummaryFailed += OnLlmFailed;
        // W1：引擎不碰 Store，由这里 bump 计数并裁定是否还值得会话内重试。
        _engine.ShouldRetryAfterFailure = nodeId =>
            _store.BumpSummaryAttempts(nodeId) < Store.MaxSummaryAttempts;
    }

    public void Start() => _watcher.Start();

    /// <summary>Bump when detection semantics change enough that history deserves a re-run.</summary>
    public const int CodenameReplayVersionCurrent = 3;

    /// <summary>
    /// One-time per replay version (mirrors macos AppDelegate.replayCodenamesIfNeeded):
    /// rebuild the dictionary from stored history oldest-first on a background task so
    /// short codes, statuses and definitions light up. The done marker
    /// (AppSettings.CodenameReplayVersion) is written only AFTER completion — a crash
    /// mid-replay re-arms. <paramref name="completion"/> always fires (immediately when no
    /// replay is due); the caller starts the watcher/engine from it so replay and watcher
    /// never write the codenames table concurrently.
    /// </summary>
    public void ReplayCodenamesIfNeeded(Action? completion = null)
    {
        if (_settings.CodenameReplayVersion >= CodenameReplayVersionCurrent)
        {
            completion?.Invoke();
            return;
        }
        Task.Run(() =>
        {
            try
            {
                ReplayCodenames(_store, _registry);
                _settings.CodenameReplayVersion = CodenameReplayVersionCurrent;
                _settings.Save();
                CodenamesChanged?.Invoke();
            }
            catch (Exception ex)
            {
                // Marker intentionally NOT written — next launch re-runs the replay.
                Log.Error("Codename lifecycle replay failed", ex);
            }
            completion?.Invoke();
        });
    }

    /// <summary>Synchronous replay core (static so the Core smoke test can drive it directly).</summary>
    public static void ReplayCodenames(Store store, CodenameRegistry registry)
    {
        store.ClearCodenames();
        registry.ReloadCache();
        foreach (var node in store.GetAllNodesAscending())
        {
            var ts = node.Command.Timestamp;
            registry.ProcessText(node.Id, ts, node.Command.Text);
            registry.RecordFromSummary(node.Summary, node.Id, ts);
            // Definitions often live in the reply — mine the stored result line too.
            if (!string.IsNullOrEmpty(node.Summary.ResultLine))
            {
                registry.ProcessText(node.Id, ts, node.Summary.ResultLine);
            }
        }
    }

    private void OnEventsParsed(IReadOnlyList<SessionEvent> events)
    {
        var codenamesTouched = false;
        foreach (var evt in events)
        {
            try
            {
                switch (evt)
                {
                    case UserCommand cmd:
                        codenamesTouched |= IngestUserCommand(cmd);
                        break;
                    case TaskComplete done:
                        if (_store.SetResultLine(done.Agent, done.SessionId, done.ResultLine, done.Timestamp) is { } nodeId)
                        {
                            NodeResultLineUpdated?.Invoke(nodeId, done.ResultLine);
                        }
                        // Mine the agent reply for codename definitions/status signals
                        // (PRD §3.3 来源覆盖), attributed to the command node it answers.
                        if (_store.LatestNodeId(done.Agent, done.SessionId, done.Timestamp) is { } target)
                        {
                            codenamesTouched |= _registry.ProcessText(
                                target, done.Timestamp, done.FullText ?? done.ResultLine);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Coordinator failed to ingest event", ex);
            }
        }
        if (codenamesTouched) CodenamesChanged?.Invoke();
    }

    private bool IngestUserCommand(UserCommand cmd)
    {
        var hash = SummaryEngine.ComputeHash(cmd);

        // Cache hit (same command seen before) → final summary immediately, no LLM call.
        var cached = _store.GetCachedSummary(hash);
        var summary = cached ?? _rule.Summarize(cmd);
        var pending = cached is null && _engine.HasLlm;

        var id = _store.InsertNode(cmd, summary, hash, pending);
        if (id < 0) return false; // duplicate (already ingested in an earlier run)

        // Rule-based mining of the raw command text (definitions → dash codes → mentions).
        var touched = _registry.ProcessText(id, cmd.Timestamp, cmd.Text);
        // On a cache hit the LLM extraction never flows through OnLlmSummarized — register it here.
        if (cached is not null)
        {
            touched |= _registry.RecordFromSummary(summary, id, cmd.Timestamp);
        }

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
        return touched;
    }

    private void OnLlmSummarized(long nodeId, string hash, Summary summary)
    {
        _store.UpdateSummary(nodeId, summary, pending: false);
        _store.CacheSummary(hash, summary);

        var node = _store.GetNode(nodeId);
        if (node is not null &&
            _registry.RecordFromSummary(summary, nodeId, node.Command.Timestamp))
        {
            CodenamesChanged?.Invoke();
        }
        NodeSummaryUpdated?.Invoke(nodeId, summary);
    }

    private void OnLlmFailed(long nodeId)
    {
        // Keep the rule summary on screen; summary_pending stays 1 → retried on next launch.
        Log.Warn($"LLM summary failed for node {nodeId}; keeping rule summary (pending retry)");
    }

    /// <summary>
    /// Re-enqueues nodes whose LLM summary never landed (called once at startup, and
    /// again after 设置 保存 via <see cref="ResetSummaryAttemptsAndRetry"/>).
    /// W1：只取 attempts 未超上限者——否则永久失败的节点每次启动都重跑烧配额。
    /// </summary>
    public void RetryPendingSummaries()
    {
        if (!_engine.HasLlm) return;
        foreach (var node in _store.GetPendingSummaries())
        {
            _engine.Enqueue(node.Id, node.Command, node.CommandHash);
        }
    }

    /// <summary>
    /// 设置「保存」后调用：清零重试计数再重新入队——换了引擎/模型/端点之后，
    /// 之前因旧配置失败到上限的节点应该获得一次新机会（对齐 mac `resetSummaryAttempts`）。
    /// </summary>
    public void ResetSummaryAttemptsAndRetry()
    {
        _store.ResetSummaryAttempts();
        RetryPendingSummaries();
    }

    public void Dispose()
    {
        _watcher.EventsParsed -= OnEventsParsed;
        _watcher.Dispose();
    }
}
