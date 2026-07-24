using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgentTimeline.Core.Summarize;

/// <summary>
/// Default engine (PRD F4.1): reuse the locally installed agent CLI, zero configuration.
///
///   claude:  claude -p &lt;prompt&gt; --output-format json --model haiku
///            → stdout is an envelope {"type":"result","result":"...", ...};
///            the summary JSON is inside the "result" string.
///   codex:   codex exec &lt;prompt&gt;  → plain text; the summary JSON is fished out of stdout.
///
/// The process runs with cwd %LOCALAPPDATA%\AgentTimeline\summarizer so the CLI's own
/// session files land in a recognizable project slug that SessionWatcher explicitly
/// ignores (feedback-loop protection). 30s timeout; any failure returns null so the
/// engine can fall back to RuleSummarizer and mark the node for retry.
/// </summary>
public sealed class CliSummarizer : ISummarizer
{
    private const int TimeoutMs = 30_000;

    private readonly AppSettings _settings;
    private string? _resolvedCli;      // full path of claude/codex executable
    private string? _resolvedKind;     // "claude" | "codex"

    public CliSummarizer(AppSettings settings) => _settings = settings;

    public string Name => $"cli:{_resolvedKind ?? _settings.CliCommand}";

    public async Task<Summary?> SummarizeAsync(UserCommand command, CancellationToken ct)
    {
        if (!TryResolveCli()) return null;
        var prompt = SummaryJson.BuildPrompt(command.Text);

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = AppPaths.SummarizerWorkDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // npm-installed CLIs are .cmd shims on Windows; those must be run through cmd.exe
        // (CreateProcess cannot execute .cmd files directly).
        var isCmdShim = _resolvedCli!.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                        _resolvedCli.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        if (isCmdShim)
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(_resolvedCli);
        }
        else
        {
            psi.FileName = _resolvedCli;
        }

        if (_resolvedKind == "claude")
        {
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(prompt);
            psi.ArgumentList.Add("--output-format");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add("haiku");
        }
        else // codex
        {
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add(prompt);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeoutMs);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                Log.Warn($"CliSummarizer: {_resolvedKind} exited {process.ExitCode}");
                return null;
            }
            return ParseCliOutput(stdout);
        }
        catch (OperationCanceledException)
        {
            Log.Warn("CliSummarizer: timed out (30s), falling back");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("CliSummarizer failed", ex);
            return null;
        }
    }

    private Summary? ParseCliOutput(string stdout)
    {
        if (_resolvedKind == "claude")
        {
            // Envelope: {"type":"result","subtype":"success","result":"<model text>",...}
            try
            {
                var start = stdout.IndexOf('{');
                if (start >= 0)
                {
                    using var doc = JsonDocument.Parse(stdout[start..]);
                    if (doc.RootElement.TryGetProperty("result", out var result) &&
                        result.ValueKind == JsonValueKind.String)
                    {
                        return SummaryJson.Parse(result.GetString() ?? "", SummarySource.Cli);
                    }
                }
            }
            catch (JsonException)
            {
                // fall through to raw parse — older CLIs printed the text directly
            }
        }
        // codex exec (or unexpected claude output): fish the JSON object out of plain text.
        return SummaryJson.Parse(stdout, SummarySource.Cli);
    }

    // ------------------------------------------------------------------ CLI discovery

    private bool TryResolveCli()
    {
        if (_resolvedCli is not null) return true;

        var preference = _settings.CliCommand;
        var candidates = preference switch
        {
            "claude" => new[] { "claude" },
            "codex" => new[] { "codex" },
            _ => new[] { "claude", "codex" }, // "auto": prefer claude, fall back to codex
        };

        foreach (var name in candidates)
        {
            var path = ResolveOnPath(name);
            if (path is not null)
            {
                _resolvedCli = path;
                _resolvedKind = name;
                Log.Info($"CliSummarizer: using {path}");
                return true;
            }
        }
        Log.Warn("CliSummarizer: no claude/codex CLI found on PATH");
        return false;
    }

    /// <summary>Searches PATH for name + {.exe,.cmd,.bat} (Process.Start does not do this for shims).</summary>
    private static string? ResolveOnPath(string name)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // Malformed PATH entry — ignore.
                }
            }
        }
        return null;
    }
}
