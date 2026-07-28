using System.Text;
using System.Threading.Channels;
using AgentTimeline.Core.Parsers;
using AgentTimeline.Interop;

namespace AgentTimeline.Core;

/// <summary>
/// Watches agent session roots with FileSystemWatcher (Windows counterpart of FSEvents)
/// and tails changed files incrementally:
///
///   - per-file (path, byte_offset, file_id) persisted in the SQLite `file_offsets` table;
///   - on change: seek to offset, read to EOF, parse only COMPLETE lines (trailing half
///     line stays unconsumed — the offset only advances past the last '\n');
///   - file_id change (delete + recreate) → offset reset to 0, full re-scan of that file;
///   - initial backfill: files with mtime within AppSettings.BackfillDays (default 7).
///
/// All file I/O and parsing runs on one background consumer task; parsed events are
/// handed to EventsParsed (consumed by TimelineCoordinator, never by UI directly).
/// </summary>
public sealed class SessionWatcher : IDisposable
{
    private readonly Store _store;
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<IAgentSessionParser> _parsers;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _cts = new();
    private Task? _consumer;

    /// <summary>Raised on the watcher thread with events parsed from one file change.</summary>
    public event Action<IReadOnlyList<SessionEvent>>? EventsParsed;

    public SessionWatcher(Store store, AppSettings settings, IReadOnlyList<IAgentSessionParser> parsers)
    {
        _store = store;
        _settings = settings;
        _parsers = parsers;
    }

    public void Start()
    {
        _consumer = Task.Run(ConsumeAsync);

        foreach (var root in WatchRoots())
        {
            // 内置三个 root 预创建：目标机器可能从没跑过对应 agent（如全新 Windows 上的
            // ~/.claude/projects），若只在 Start 时 Exists 检查一次，之后装了 agent 也
            // 永远不会被监听，必须重启 app 才恢复（M3 实机审计发现）。目录都在用户目录
            // 下，创建无副作用；zcode 自定义路径不代建（用户可能填错盘符）。
            if (IsBuiltinRoot(root))
            {
                try { Directory.CreateDirectory(root); }
                catch (Exception ex) { Log.Error($"Failed to create watch root {root}", ex); }
            }
            if (!Directory.Exists(root)) continue;
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    Filter = "*.jsonl",
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Changed += (_, e) => Enqueue(e.FullPath);
                watcher.Created += (_, e) => Enqueue(e.FullPath);
                watcher.Renamed += (_, e) => Enqueue(e.FullPath);
                // 缓冲溢出（事件风暴）时变更会被静默丢弃；用一次幂等补扫兜底，
                // 否则缺口要等下次重启回填才补上。
                var rootCopy = root;
                watcher.Error += (_, e) =>
                {
                    Log.Warn($"Watcher buffer overflow on {rootCopy}: {e.GetException().Message}; rescanning");
                    Task.Run(() => BackfillRoot(rootCopy));
                };
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
                Log.Info($"Watching {root}");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to watch {root}", ex);
            }
        }

        // Backfill runs through the same queue so ordering/dedup logic is uniform.
        Task.Run(Backfill);
    }

    private IEnumerable<string> WatchRoots()
    {
        if (_settings.EnableClaude) yield return AppPaths.ClaudeProjectsRoot;
        if (_settings.EnableCodex) yield return AppPaths.CodexSessionsRoot;
        if (_settings.EnableGrok) yield return AppPaths.GrokSessionsRoot;
        if (_settings.EnableKimi) yield return AppPaths.KimiSessionsRoot;
        if (_settings.EnableZcode)
        {
            // 空 = 用实机确认的默认根 ~\.zcode\cli\agents；填了则用自定义路径。
            yield return string.IsNullOrWhiteSpace(_settings.ZcodeSessionRoot)
                ? AppPaths.ZcodeAgentsRootDefault
                : _settings.ZcodeSessionRoot;
        }
    }

    // 自定义 zcode 路径不算内置（用户可能填错盘符，不代建目录）。
    private static bool IsBuiltinRoot(string root) =>
        root == AppPaths.ClaudeProjectsRoot ||
        root == AppPaths.CodexSessionsRoot ||
        root == AppPaths.GrokSessionsRoot ||
        root == AppPaths.KimiSessionsRoot ||
        root == AppPaths.ZcodeAgentsRootDefault;

    private void Backfill()
    {
        foreach (var root in WatchRoots()) BackfillRoot(root);
    }

    private void BackfillRoot(string root)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(0, _settings.BackfillDays));
        if (!Directory.Exists(root)) return;
        try
        {
            var files = Directory
                .EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .Where(f => f.LastWriteTimeUtc >= cutoff)
                .OrderBy(f => f.LastWriteTimeUtc); // oldest first → timeline fills chronologically
            foreach (var file in files) Enqueue(file.FullName);
        }
        catch (Exception ex)
        {
            Log.Error($"Backfill scan failed for {root}", ex);
        }
    }

    private void Enqueue(string path)
    {
        if (ShouldIgnore(path)) return;
        _queue.Writer.TryWrite(path);
    }

    /// <summary>
    /// The summarizer invokes `claude -p` with cwd %LOCALAPPDATA%\AgentTimeline\summarizer,
    /// which makes Claude Code write ITS session files under a project slug containing
    /// "AgentTimeline-summarizer". Watching those would loop forever — ignore them.
    /// </summary>
    private static bool ShouldIgnore(string path) =>
        path.Contains("AgentTimeline", StringComparison.OrdinalIgnoreCase) &&
        path.Contains("summarizer", StringComparison.OrdinalIgnoreCase);

    private async Task ConsumeAsync()
    {
        // Small debounce window: session files get bursts of appends; coalesce duplicates.
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
        {
            var batch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.TryRead(out var path)) batch.Add(path);
            try
            {
                await Task.Delay(200, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            while (reader.TryRead(out var more)) batch.Add(more);

            foreach (var path in batch)
            {
                try
                {
                    ProcessFile(path);
                }
                catch (IOException)
                {
                    // File busy — a follow-up Changed event will retry.
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to process {path}", ex);
                }
            }
        }
    }

    private void ProcessFile(string path)
    {
        var parser = _parsers.FirstOrDefault(p => p.CanHandle(path));
        if (parser is null || !File.Exists(path)) return;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var fileId = FileIdentity.GetFileId(stream);
        long offset = 0;
        var stored = _store.GetFileOffset(path);
        if (stored is { } s)
        {
            offset = s.Offset;
            // File recreated (id changed) or truncated → rescan from 0.
            if ((s.FileId.Length > 0 && fileId.Length > 0 && s.FileId != fileId) ||
                offset > stream.Length)
            {
                offset = 0;
            }
        }
        if (offset >= stream.Length) return; // nothing new

        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - offset];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }

        // Only consume complete lines; the trailing half line waits for the next event.
        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
        if (lastNewline < 0) return;
        var consumed = lastNewline + 1;

        var lines = SplitLines(buffer, consumed, offset);
        var events = parser.ParseLines(path, lines);

        // 崩溃安全顺序：先入库（订阅方同步写 store，nodes 表 UNIQUE 约束保证重放幂等），
        // 成功后才推进偏移——反过来会在「偏移已推进、事件未入库」窗口里静默丢数据。
        if (events.Count > 0) EventsParsed?.Invoke(events);
        _store.SetFileOffset(path, offset + consumed, fileId);
    }

    /// <summary>Splits a byte range on '\n', tracking each line's absolute byte offset.</summary>
    private static List<RawLine> SplitLines(byte[] buffer, int length, long baseOffset)
    {
        var lines = new List<RawLine>();
        var lineStart = 0;
        for (var i = 0; i < length; i++)
        {
            if (buffer[i] != (byte)'\n') continue;
            var lineLength = i - lineStart;
            if (lineLength > 0 && buffer[lineStart + lineLength - 1] == (byte)'\r') lineLength--;
            if (lineLength > 0)
            {
                lines.Add(new RawLine(
                    baseOffset + lineStart,
                    Encoding.UTF8.GetString(buffer, lineStart, lineLength)));
            }
            lineStart = i + 1;
        }
        return lines;
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        try { _consumer?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        _cts.Dispose();
    }
}
