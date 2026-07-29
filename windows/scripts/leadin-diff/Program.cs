// 引子续接（docs/TEXT-NORMALIZATION.md §3.3b）的差分执行工具。
//
//   LeadInDiff extract  <语料.tsv> [agent,agent…]     从本机 session 文件抽语料（默认 claude,codex）
//   LeadInDiff excerpt  <语料.tsv> <产出.tsv>          对语料逐条跑 ParserUtil.ResultExcerpt
//   LeadInDiff compare  <前.tsv> <后.tsv> <差异样本.txt>  出指标表
//   LeadInDiff residual <语料.tsv> <后.tsv> <分类.txt>   给「续接后仍以冒号收尾」的分桶归因
//
// 为什么分三步而不是一步跑完：
//
//   1. **语料必须先冻结**——`.claude\projects` / `.codex\sessions` 正在被真实 agent 会话
//      写入。若「前」「后」两次各自去扫盘，两次读到的语料就不是同一份，差分结果无意义。
//      extract 只跑一次，产出一份快照，两个源码状态都吃这一份。
//   2. **抽语料走真实解析器**（ClaudeParser/CodexParser…的 ParseLines），口径与产品完全
//      一致：注入块过滤、summarizer scratch 目录整文件禁用、session_meta 处理都在里面。
//      TaskComplete.FullText 恰是喂给 ResultExcerpt 的那个字符串（五个解析器逐一核对过），
//      所以 ResultExcerpt(FullText) 就是产品会写进库的结果行。
//   3. **extract 用哪个源码状态都行**——本次改动只动了 ResultExcerpt 与
//      TextNormalizer.StripLeadingMarkers（后者是纯提取重构，Normalize 语义不变），
//      抽取路径逐字节相同。
//
// 语料含真实项目名与命令原文：产出只写临时目录，**不要入仓**。
using System.Globalization;
using System.Text;
using AgentTimeline.Core;
using AgentTimeline.Core.Parsers;

namespace AgentTimeline.LeadInDiff;

internal static class LeadInDiffTool
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0) return Usage();
        try
        {
            switch (args[0])
            {
                case "extract":
                    if (args.Length < 2) return Usage();
                    return Extract(args[1], args.Length > 2 ? args[2] : "claude,codex");
                case "excerpt":
                    if (args.Length < 3) return Usage();
                    return Excerpt(args[1], args[2]);
                case "compare":
                    if (args.Length < 4) return Usage();
                    return Compare(args[1], args[2], args[3]);
                case "residual":
                    if (args.Length < 4) return Usage();
                    return Residual(args[1], args[2], args[3]);
                default:
                    return Usage();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"失败: {ex}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "用法:\n" +
            "  LeadInDiff extract  <语料.tsv> [claude,codex,grok,kimi,zcode]\n" +
            "  LeadInDiff excerpt  <语料.tsv> <产出.tsv>\n" +
            "  LeadInDiff compare  <前.tsv> <后.tsv> <差异样本.txt>\n" +
            "  LeadInDiff residual <语料.tsv> <后.tsv> <分类.txt>");
        return 2;
    }

    // ── 抽语料 ──────────────────────────────────────────────────────────────

    /// <summary>一个 agent 的语料来源：会话根目录 + 文件名模式 + 它的解析器。</summary>
    private readonly record struct Source(string Agent, string Root, string Pattern, IAgentSessionParser Parser);

    private static IEnumerable<Source> SourcesFor(string agents)
    {
        foreach (var raw in agents.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.Trim().ToLowerInvariant();
            switch (name)
            {
                case "claude":
                    yield return new Source(name, AppPaths.ClaudeProjectsRoot, "*.jsonl", new ClaudeParser());
                    break;
                case "codex":
                    yield return new Source(name, AppPaths.CodexSessionsRoot, "rollout-*.jsonl", new CodexParser());
                    break;
                case "grok":
                    yield return new Source(name, AppPaths.GrokSessionsRoot, "updates.jsonl", new GrokParser());
                    break;
                case "kimi":
                    yield return new Source(name, AppPaths.KimiSessionsRoot, "wire.jsonl", new KimiParser());
                    break;
                case "zcode":
                    yield return new Source(name, AppPaths.ZcodeAgentsRootDefault, "transcript.jsonl", new ZcodeParser());
                    break;
                default:
                    throw new ArgumentException($"未知 agent: {name}");
            }
        }
    }

    /// <summary>一次喂给解析器的行数——整文件读进内存会被个别超大 rollout 撑爆。</summary>
    private const int BatchLines = 2000;

    private static int Extract(string outPath, string agents)
    {
        using var writer = new StreamWriter(outPath, false, new UTF8Encoding(false));
        var grand = 0;
        foreach (var src in SourcesFor(agents))
        {
            if (!Directory.Exists(src.Root))
            {
                Console.WriteLine($"{src.Agent,-7} 根目录不存在，跳过: {src.Root}");
                continue;
            }
            int files = 0, replies = 0, skipped = 0;
            foreach (var path in SafeEnumerate(src.Root, src.Pattern))
            {
                if (!src.Parser.CanHandle(path)) continue;
                files++;
                try
                {
                    foreach (var ev in ParseWholeFile(src.Parser, path))
                    {
                        if (ev is not TaskComplete tc || tc.FullText is null) continue;
                        writer.Write(src.Agent);
                        writer.Write('\t');
                        writer.Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(path)));
                        writer.Write('\t');
                        writer.Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(tc.FullText)));
                        writer.Write('\n');
                        replies++;
                    }
                }
                catch (IOException) { skipped++; }              // 正被 agent 写入 / 已删除
                catch (UnauthorizedAccessException) { skipped++; }
            }
            grand += replies;
            Console.WriteLine($"{src.Agent,-7} 文件 {files,5}  回复 {replies,6}" +
                              (skipped > 0 ? $"  （{skipped} 个文件读不动，已跳过）" : ""));
        }
        Console.WriteLine($"合计 {grand} 条真实 agent 回复 → {outPath}");
        return grand > 0 ? 0 : 1;
    }

    /// <summary>目录树里个别子目录可能没权限——枚举本身不能因此整个炸掉。</summary>
    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        return Directory.EnumerateFiles(root, pattern, options);
    }

    /// <summary>
    /// 整文件走一遍解析器。分批喂行与 SessionWatcher 的增量 tail 同构（解析器本来就
    /// 要能被多次 ParseLines 调用），偏移量按 UTF-8 字节累计——ResultExcerpt 不看偏移，
    /// 但保持与产品同形态更省心。
    /// </summary>
    private static IEnumerable<SessionEvent> ParseWholeFile(IAgentSessionParser parser, string path)
    {
        var batch = new List<RawLine>(BatchLines);
        long offset = 0;
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            batch.Add(new RawLine(offset, line));
            offset += Encoding.UTF8.GetByteCount(line) + 1;
            if (batch.Count < BatchLines) continue;
            foreach (var ev in parser.ParseLines(path, batch)) yield return ev;
            batch.Clear();
        }
        if (batch.Count > 0)
        {
            foreach (var ev in parser.ParseLines(path, batch)) yield return ev;
        }
    }

    // ── 逐条摘录 ────────────────────────────────────────────────────────────

    private static int Excerpt(string corpusPath, string outPath)
    {
        using var writer = new StreamWriter(outPath, false, new UTF8Encoding(false));
        var n = 0;
        foreach (var (agent, _, text) in ReadCorpus(corpusPath))
        {
            var excerpt = ParserUtil.ResultExcerpt(text);
            writer.Write(agent);
            writer.Write('\t');
            writer.Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(excerpt)));
            writer.Write('\n');
            n++;
        }
        Console.WriteLine($"摘录 {n} 条 → {outPath}");
        return 0;
    }

    private static IEnumerable<(string Agent, string Path, string Text)> ReadCorpus(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            var a = line.IndexOf('\t');
            var b = line.IndexOf('\t', a + 1);
            yield return (line[..a],
                          Encoding.UTF8.GetString(Convert.FromBase64String(line[(a + 1)..b])),
                          Encoding.UTF8.GetString(Convert.FromBase64String(line[(b + 1)..])));
        }
    }

    // ── 出指标 ──────────────────────────────────────────────────────────────

    private static bool EndsWithColon(string s)
    {
        var t = s.TrimEnd();
        return t.EndsWith(':') || t.EndsWith('：');
    }

    private static int Compare(string beforePath, string afterPath, string samplesPath)
    {
        var before = ReadExcerpts(beforePath);
        var after = ReadExcerpts(afterPath);
        if (before.Count != after.Count)
        {
            Console.Error.WriteLine($"❌ 前后条数不一致 {before.Count} vs {after.Count}——语料没冻结住，结果作废");
            return 1;
        }

        int changed = 0, shorter = 0, prefixViolations = 0;
        int colonBefore = 0, colonAfter = 0, emptyBefore = 0, emptyAfter = 0;
        long lenBefore = 0, lenAfter = 0;
        var samples = new StringBuilder();
        var violations = new StringBuilder();
        var shown = 0;

        for (var i = 0; i < before.Count; i++)
        {
            var (agent, b) = before[i];
            var a = after[i].Text;
            lenBefore += b.Length;
            lenAfter += a.Length;
            if (EndsWithColon(b)) colonBefore++;
            if (EndsWithColon(a)) colonAfter++;
            if (b.Length == 0) emptyBefore++;
            if (a.Length == 0) emptyAfter++;
            if (b == a) continue;

            changed++;
            if (a.Length < b.Length) shorter++;
            // 硬约束：只可能加内容，不可能改内容 → 旧值必须是新值的前缀。
            if (!a.StartsWith(b, StringComparison.Ordinal))
            {
                prefixViolations++;
                violations.Append("── 违反「旧值是新值前缀」 #").Append(prefixViolations)
                          .Append("  agent=").Append(agent).Append('\n')
                          .Append("旧(").Append(b.Length).Append("): ").Append(b).Append('\n')
                          .Append("新(").Append(a.Length).Append("): ").Append(a).Append("\n\n");
            }
            else if (shown < 10)
            {
                shown++;
                samples.Append("── 变化样本 #").Append(shown).Append("  agent=").Append(agent).Append('\n')
                       .Append("旧(").Append(b.Length).Append("): ").Append(b).Append('\n')
                       .Append("新(").Append(a.Length).Append("): ").Append(a).Append("\n\n");
            }
        }

        File.WriteAllText(samplesPath, violations.ToString() + samples.ToString(), new UTF8Encoding(false));

        var n = before.Count;
        string Pct(int v) => (100.0 * v / n).ToString("F1", CultureInfo.InvariantCulture);
        string Avg(long v) => ((double)v / n).ToString("F0", CultureInfo.InvariantCulture);

        Console.WriteLine();
        Console.WriteLine($"| 指标 | 实测（{n} 条真实 agent 回复） |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| 产出变化条数 / 占比 | {changed} / {Pct(changed)}% |");
        Console.WriteLine($"| 变短条数（回归） | {shorter} |");
        Console.WriteLine($"| 旧值是新值的前缀 | {(prefixViolations == 0 ? "全部成立" : $"❌ {prefixViolations} 条不成立")} |");
        Console.WriteLine($"| 冒号结尾条数 前→后 | {colonBefore} → {colonAfter} |");
        Console.WriteLine($"| 平均长度 前→后 | {Avg(lenBefore)} → {Avg(lenAfter)} |");
        Console.WriteLine($"| 空串 前→后 | {emptyBefore} → {emptyAfter} |");
        Console.WriteLine();
        Console.WriteLine($"样本/违例: {samplesPath}");

        // 两条硬约束任一不成立 → 非零退出，编排脚本会停下来。
        return (shorter == 0 && prefixViolations == 0) ? 0 : 1;
    }

    // ── 残留归因 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 「续接后仍以冒号收尾」的分桶归因。
    ///
    /// mac 语料上这个残留是 2 条，Windows 语料上是四位数——差得太远，必须查明是
    /// 实现少接了，还是**本来就没得接**。三个桶：
    ///
    ///   ① 回复只有一段          —— 引子后面根本没有下一段，规则无从续接；
    ///   ② 后续段被 Excerpt 档丢弃 —— 正文全在代码围栏/表格里，规整层按 §3.3 整块删除，
    ///                              没有散文可接（mac 那 2 条就是这一类）；
    ///   ③ 续接后末段仍以冒号收尾 —— 引子链吃到段数/长度上限停下。
    ///
    /// ①②说明规则已尽力，③说明是兜底生效。三桶之外还有剩余就是实现有问题。
    /// 分段用的空行切分是**诊断口径**（不碰被测的 ParserUtil 私有实现），
    /// 只用于归类，不参与任何硬约束判定。
    /// </summary>
    private static int Residual(string corpusPath, string afterPath, string outPath)
    {
        var after = ReadExcerpts(afterPath);
        var buckets = new Dictionary<string, int>
        {
            ["① 回复只有一段（引子后无正文）"] = 0,
            ["② 后续段被 Excerpt 档丢弃（围栏/表格）"] = 0,
            ["③ 续接后末段仍以冒号收尾（吃到上限）"] = 0,
            ["④ 未归类"] = 0,
        };
        var samples = new Dictionary<string, StringBuilder>();
        foreach (var k in buckets.Keys) samples[k] = new StringBuilder();

        var i = -1;
        var total = 0;
        foreach (var (_, _, raw) in ReadCorpus(corpusPath))
        {
            i++;
            if (i >= after.Count) break;
            var excerpt = after[i].Text;
            if (!EndsWithColon(excerpt)) continue;
            total++;

            var rawParas = CountParagraphs(raw);
            var normParas = CountParagraphs(
                Core.Text.TextNormalizer.Normalize(raw, Core.Text.NormalizeProfile.Excerpt));

            string bucket;
            if (normParas > 1) bucket = "③ 续接后末段仍以冒号收尾（吃到上限）";
            else if (rawParas > 1) bucket = "② 后续段被 Excerpt 档丢弃（围栏/表格）";
            else if (rawParas == 1) bucket = "① 回复只有一段（引子后无正文）";
            else bucket = "④ 未归类";

            buckets[bucket]++;
            var sb = samples[bucket];
            if (buckets[bucket] <= 3)
            {
                sb.Append("  样本: ").Append(excerpt.Length > 160 ? excerpt[..160] + "…" : excerpt).Append('\n');
            }
        }

        var report = new StringBuilder();
        report.Append("续接后仍以冒号收尾: ").Append(total).Append(" 条\n\n");
        foreach (var kv in buckets)
        {
            report.Append(kv.Key).Append(": ").Append(kv.Value).Append('\n');
            report.Append(samples[kv.Key]).Append('\n');
        }
        File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(false));
        Console.WriteLine(report.ToString());
        return 0;
    }

    /// <summary>诊断用的空行切段计数（不含空段）。</summary>
    private static int CountParagraphs(string text)
    {
        var n = 0;
        var inPara = false;
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.Trim().Length == 0) { inPara = false; continue; }
            if (!inPara) { n++; inPara = true; }
        }
        return n;
    }

    private static List<(string Agent, string Text)> ReadExcerpts(string path)
    {
        var list = new List<(string, string)>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            var t = line.IndexOf('\t');
            list.Add((line[..t], Encoding.UTF8.GetString(Convert.FromBase64String(line[(t + 1)..]))));
        }
        return list;
    }
}
