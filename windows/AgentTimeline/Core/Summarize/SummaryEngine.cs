using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace AgentTimeline.Core.Summarize;

/// <summary>
/// Summary orchestration (PRD F4): serial queue + rate limiting so the local CLI is never
/// hammered; results cached in SQLite by command hash; failures degrade to the rule-based
/// summary and leave the node marked pending for retry.
///
/// The engine only COMPUTES — persistence stays in TimelineCoordinator (single write point),
/// which subscribes to <see cref="Summarized"/>.
/// </summary>
public sealed class SummaryEngine : IDisposable
{
    private static readonly TimeSpan MinCallInterval = TimeSpan.FromSeconds(1.5);

    /// <summary>失败后再次入队前的退避（对齐 mac 的 1s）。</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly AppSettings _settings;

    // W2 最新优先：回填数百节点时，用户盯着的顶部最新节点不该最后才拿到 LLM 标题。
    // Channel 退化为「有活干」的唤醒信号，实际取件走按 ts 降序的优先队列。
    private readonly PriorityQueue<PendingItem, long> _pending = new();
    private readonly HashSet<long> _queuedIds = new();
    private readonly object _pendingGate = new();
    private readonly Channel<byte> _wakeup = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private ISummarizer? _llm;

    private readonly record struct PendingItem(long NodeId, UserCommand Command, string Hash);

    /// <summary>(nodeId, hash, summary) — raised on the worker thread when an LLM summary lands.</summary>
    public event Action<long, string, Summary>? Summarized;

    /// <summary>
    /// (nodeId) — LLM 路径失败；节点保留规则摘要。订阅方（Coordinator）负责 bump
    /// attempts 并决定是否值得重试，返回 true 表示"还可以再试"。
    /// </summary>
    public event Action<long>? SummaryFailed;

    /// <summary>
    /// 失败重试判定钩子（W1）：由 Coordinator 注入——它持有 Store，bump 计数后
    /// 返回是否仍在 <see cref="Store.MaxSummaryAttempts"/> 之内。未注入时不重试
    /// （保持旧行为，测试友好）。
    /// </summary>
    public Func<long, bool>? ShouldRetryAfterFailure { get; set; }

    public SummaryEngine(AppSettings settings)
    {
        _settings = settings;
        ReloadSummarizer();
        _worker = Task.Run(WorkAsync);
    }

    /// <summary>True when an LLM-backed summarizer is configured (nodes should be enqueued).</summary>
    public bool HasLlm => _llm is not null;

    /// <summary>Re-picks the summarizer after 设置 changes.</summary>
    public void ReloadSummarizer()
    {
        _llm = _settings.Engine switch
        {
            SummaryEngineKind.Cli => new CliSummarizer(_settings),
            SummaryEngineKind.Provider => new ProviderSummarizer(_settings),
            _ => null, // Rule: instant summaries only, nothing queued
        };
    }

    public static string ComputeHash(UserCommand command)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{command.Agent.Key()}\n{command.Text}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public void Enqueue(long nodeId, UserCommand command, string hash)
    {
        if (!HasLlm) return;
        lock (_pendingGate)
        {
            if (!_queuedIds.Add(nodeId)) return; // 已在队列里，别排两次
            // 优先级 = -ts：PriorityQueue 取最小值 → 时间戳最大者先出（最新优先）。
            _pending.Enqueue(new PendingItem(nodeId, command, hash),
                -command.Timestamp.ToUnixTimeMilliseconds());
        }
        _wakeup.Writer.TryWrite(0);
    }

    private bool TryDequeue(out PendingItem item)
    {
        lock (_pendingGate)
        {
            if (_pending.TryDequeue(out item, out _))
            {
                _queuedIds.Remove(item.NodeId);
                return true;
            }
            return false;
        }
    }

    private async Task WorkAsync()
    {
        var reader = _wakeup.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                reader.TryRead(out _); // 消费唤醒信号；实际取件走优先队列
                while (TryDequeue(out var item))
                {
                    var llm = _llm;
                    if (llm is null) continue;
                    var failed = false;
                    try
                    {
                        var summary = await llm.SummarizeAsync(item.Command, _cts.Token)
                            .ConfigureAwait(false);
                        if (summary is not null)
                        {
                            Summarized?.Invoke(item.NodeId, item.Hash, summary);
                        }
                        else
                        {
                            failed = true;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"SummaryEngine: {llm.Name} threw", ex);
                        failed = true;
                    }

                    if (failed)
                    {
                        SummaryFailed?.Invoke(item.NodeId);
                        // W1 会话内重试：Coordinator bump 计数后判定是否仍在上限内，
                        // 是则退避后重新入队——否则超时一次就得重启 App 才会再试。
                        if (ShouldRetryAfterFailure?.Invoke(item.NodeId) == true)
                        {
                            try
                            {
                                await Task.Delay(RetryDelay, _cts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) { return; }
                            Enqueue(item.NodeId, item.Command, item.Hash);
                        }
                    }

                    // Rate limit between LLM calls.
                    try
                    {
                        await Task.Delay(MinCallInterval, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _wakeup.Writer.TryComplete();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        _cts.Dispose();
    }
}
