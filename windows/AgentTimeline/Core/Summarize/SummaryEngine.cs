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

    private readonly AppSettings _settings;
    private readonly Channel<(long NodeId, UserCommand Command, string Hash)> _queue =
        Channel.CreateUnbounded<(long, UserCommand, string)>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private ISummarizer? _llm;

    /// <summary>(nodeId, hash, summary) — raised on the worker thread when an LLM summary lands.</summary>
    public event Action<long, string, Summary>? Summarized;

    /// <summary>(nodeId) — raised when the LLM path failed; the node keeps its rule summary, pending retry.</summary>
    public event Action<long>? SummaryFailed;

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
        _queue.Writer.TryWrite((nodeId, command, hash));
    }

    private async Task WorkAsync()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    var llm = _llm;
                    if (llm is null) continue;
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
                            SummaryFailed?.Invoke(item.NodeId);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"SummaryEngine: {llm.Name} threw", ex);
                        SummaryFailed?.Invoke(item.NodeId);
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
        _queue.Writer.TryComplete();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        _cts.Dispose();
    }
}
