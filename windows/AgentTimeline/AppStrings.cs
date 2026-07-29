using System.Globalization;
using System.Text.Json;
using AgentTimeline.Core;

namespace AgentTimeline;

/// <summary>用户可选的界面语言。<see cref="System"/> = 跟随操作系统。</summary>
public enum AppLanguage
{
    System,
    ZhHans,
    En,
    Ja,
    Ko,
}

/// <summary>
/// 界面文案（design/strings.json 的双端共享表，Assets 下是它的字节一致副本）。
///
/// 为什么不用 .resw：语言由**应用内设置**决定，不依赖系统资源解析；两端又有大量代码
/// 构建的 UI（chip 弹层、词典面板、托盘菜单），原生资源在那些地方同样要手写查表。
/// 共享 JSON 则能一份文件译四语 + CI 硬校验键集合，与 design-tokens.json 同一套范式。
///
/// **平台覆盖**：键名可带 <c>@win</c> / <c>@mac</c> 后缀，查找时先试 <c>键名@win</c>
/// 再回退 <c>键名</c>。只在概念本身分叉时才拆（如 hideToTray——macOS 是菜单栏应用，
/// 没有托盘），不要为措辞差异拆键，那正是这张表要消灭的漂移。
///
/// **占位符**用 <c>{0}</c>/<c>{1}</c> 序号式，两端 <see cref="Format"/> 语义相同；
/// 不要写各自语言的插值语法，否则同一份表在另一端就成了字面量。
/// </summary>
public sealed class AppStrings
{
    private const string PlatformSuffix = "@win";
    private const string Fallback = "en";

    private readonly Dictionary<string, Dictionary<string, string>> _table;

    /// <summary>当前生效的语言标签（"zh-Hans" / "en" / "ja" / "ko"），已解析过「跟随系统」。</summary>
    public string Language { get; }

    /// <summary>
    /// 与 <see cref="Language"/> 对应的 <see cref="CultureInfo"/>，供日期/数字格式化用
    /// （日期分隔线的 "MM-dd · ddd" 星期缩写、数字分组）。
    ///
    /// 不去改 <c>CultureInfo.CurrentUICulture</c>：那是进程级全局量，会顺带改掉
    /// 后台线程的解析行为（<c>double.Parse</c> 之类），而本应用大量读写 JSON /
    /// SQLite 数值。只在**格式化调用点**显式传入，作用域可控。
    /// </summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.InvariantCulture;

    public static AppStrings Current { get; private set; } = Empty();

    /// <summary>语言切换后触发，供 UI 重建代码构建的部分（托盘菜单等不会自动刷新）。</summary>
    public static event Action? Changed;

    private AppStrings(Dictionary<string, Dictionary<string, string>> table, string language)
    {
        _table = table;
        Language = language;
    }

    private static AppStrings Empty() => new(new(), Fallback);

    /// <summary>
    /// 载入文案表并解析语言。解析失败不抛——挂件不该因为一份文案表读不动就起不来，
    /// 退化成「键名原样显示」比白屏好排查（与 DesignTokens.Load 同一姿态）。
    /// </summary>
    public static void Load(AppLanguage preference)
    {
        var language = Resolve(preference);
        Culture = ToCulture(language);
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "strings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var table = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var entry in doc.RootElement.GetProperty("strings").EnumerateObject())
            {
                var langs = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var value in entry.Value.EnumerateObject())
                {
                    langs[value.Name] = value.Value.GetString() ?? "";
                }
                table[entry.Name] = langs;
            }
            Current = new AppStrings(table, language);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load strings.json; falling back to key names", ex);
            Current = new AppStrings(new(), language);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// 「跟随系统」时按系统 UI 语言取最接近的一档。zh-TW / zh-HK 也归到 zh-Hans——
    /// 目前只有简体一份，给繁体用户简体也好过直接掉到英文。
    /// </summary>
    internal static string Resolve(AppLanguage preference) => preference switch
    {
        AppLanguage.ZhHans => "zh-Hans",
        AppLanguage.En => "en",
        AppLanguage.Ja => "ja",
        AppLanguage.Ko => "ko",
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            "zh" => "zh-Hans",
            "ja" => "ja",
            "ko" => "ko",
            _ => Fallback,
        },
    };

    /// <summary>
    /// 语言标签 → 格式化用文化。取不到（精简运行时未带该文化数据）就退回不变文化，
    /// 日期照样出得来、只是星期缩写变英文——比抛异常让面板起不来强。
    /// </summary>
    private static CultureInfo ToCulture(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language == "zh-Hans" ? "zh-CN" : language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    /// <summary>
    /// 取文案。查不到时**回显键名**而不是空串：空串会让界面看起来"少了个控件"，
    /// 键名则一眼看出是漏了哪个键（这类缺失只有跑起来才暴露，得让它自曝）。
    /// </summary>
    public string Get(string key)
    {
        if (_table.TryGetValue(key + PlatformSuffix, out var platform) && Pick(platform) is { } p) return p;
        if (_table.TryGetValue(key, out var langs) && Pick(langs) is { } v) return v;
        return key;

        string? Pick(Dictionary<string, string> langs) =>
            langs.TryGetValue(Language, out var hit) && hit.Length > 0 ? hit
            : langs.TryGetValue(Fallback, out var en) && en.Length > 0 ? en
            : null;
    }

    /// <summary>序号占位符替换（{0}/{1}…）。与 mac 端 Strings.format 逐字同语义。</summary>
    public string Format(string key, params object?[] args)
    {
        var text = Get(key);
        for (var i = 0; i < args.Length; i++)
        {
            text = text.Replace("{" + i + "}", args[i]?.ToString() ?? "", StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>快捷取用：<c>AppStrings.S("tray.exit")</c>。</summary>
    public static string S(string key) => Current.Get(key);

    public static string F(string key, params object?[] args) => Current.Format(key, args);
}
