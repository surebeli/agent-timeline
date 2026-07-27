namespace AgentTimeline.Core;

/// <summary>
/// 展示态截断常量的单一出处（docs/TEXT-NORMALIZATION.md §4-5、§5.1 P4）。
/// **与 mac 端 `Core/DisplayLimits.swift` 同表——改这里必须同步改那边。**
///
/// 设计原则：**存储只留护栏，显示层负责钳制**（PRD L3 视觉钳制）。
/// 这里的数字不是排版决策，而是防御性上限——防止畸形输入把一行撑成一兆，
/// 正常内容永远不该碰到它们。"一屏显示多少"完全交给显示层（折叠一行 /
/// 展开全展开 / hover tooltip 兜全文），因此双端可以用同一张表，
/// 而两端 UI 各按自己的排版钳制，互不牵制。
///
/// 定档依据（mac 端 431 节点实测，2026-07-27）：标题长度 p50=14 / p90=25 /
/// max=41；旧的 mac 40 上限只在 0.7% 的节点上触发、60 的 LLM 上限一次都没触发；
/// 而 win 旧值 20 会咬掉约 15% 的标题。护栏取 120 ≈ max 的 3 倍。
///
/// 计量口径：win 按 UTF-16 code unit（<see cref="Parsers.ParserUtil.Clip"/>），
/// mac 按 grapheme cluster。护栏水位下这点差异永远不会被触发。
///
/// 旧数据不迁移（同 docs §5.2-2）：已存库的截断值保持原样，新数据走新护栏。
/// </summary>
public static class DisplayLimits
{
    /// <summary>摘要标题：规则档与 LLM 档同值——它们渲染在同一个 UI 槽位里。</summary>
    public const int SummaryTitle = 120;

    /// <summary>摘要要点：单条上限。</summary>
    public const int KeyPoint = 200;

    /// <summary>摘要要点：条数上限。</summary>
    public const int KeyPointCount = 6;

    /// <summary>LLM 提取的代号定义（chip flyout / 词典面板内全文展示）。</summary>
    public const int CodenameDefinition = 120;

    /// <summary>结果行：规整 → 首段 → 此上限（docs §3.1 Excerpt 档）。</summary>
    public const int ResultLine = 500;

    /// <summary>LLM prompt 输入上限。</summary>
    public const int PromptInput = 4000;
}
