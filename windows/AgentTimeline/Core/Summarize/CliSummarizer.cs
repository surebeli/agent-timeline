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
            // prompt 经 stdin 传递而非命令行参数：.cmd shim 要过 cmd.exe，而 cmd 不认
            // C 运行时的反斜杠引号转义——prompt 里 JSON 模板的引号会翻转 cmd 引号状态、
            // 换行会被当命令分隔符（BatBadBut 同类），命令行路径在 shim 下必坏且有注入面。
            // claude -p 无位置参数时从 stdin 读；codex exec 用 "-" 显式读 stdin。
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // npm-installed CLIs are .cmd shims on Windows; those must be run through cmd.exe
        // (CreateProcess cannot execute .cmd files directly). 保留的参数全部为固定 ASCII。
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
            psi.ArgumentList.Add("--output-format");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add("haiku");
        }
        else // codex
        {
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add("-");
        }

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null) return null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeoutMs);

            await process.StandardInput.WriteAsync(prompt.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            process.StandardInput.Close();

            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            // 按行读而非等进程退出：用户的 claude 可能挂 SessionEnd 等 hook，结果信封早已
            // 打到 stdout 而进程被 hook 拖住不退出——等 WaitForExit 会把到手的结果随超时
            // 一起丢掉（实机 M3：手动 14s 出结果、in-app 恒 30s 超时）。claude 单行 JSON
            // 信封（"type":"result"）一到即收针，finally 里杀树顺带收掉挂着的 hook。
            var sb = new StringBuilder();
            var gotClaudeResult = false;
            while (await process.StandardOutput.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false)
                   is { } line)
            {
                sb.AppendLine(line);
                if (_resolvedKind == "claude" && line.Contains("\"type\":\"result\""))
                {
                    gotClaudeResult = true;
                    break;
                }
            }
            var stdout = sb.ToString();

            if (!gotClaudeResult)
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                _ = await stderrTask.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    Log.Warn($"CliSummarizer: {_resolvedKind} exited {process.ExitCode}");
                    return null;
                }
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
        finally
        {
            // 超时/取消路径必须杀整棵进程树：Dispose 不终止进程，且 shim 场景下真正干活的
            // node.exe 是孙进程，普通 Kill 只杀 cmd.exe——不杀树会积累僵尸并持续烧配额。
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2_000);
                }
            }
            catch { /* already gone */ }
            process?.Dispose();
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
                    // Windows PATH 条目可合法带包裹引号（"C:\Program Files\nodejs\"），
                    // 不去引号 Path.Combine 后 File.Exists 恒 false，shim 静默漏检。
                    var candidate = Path.Combine(dir.Trim().Trim('"'), name + ext);
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
