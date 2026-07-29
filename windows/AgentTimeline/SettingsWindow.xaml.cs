using AgentTimeline.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace AgentTimeline;

/// <summary>
/// Settings window (PRD F6). Values are loaded into the controls on construction and
/// written back to App.Settings on 保存 — with live re-application of window behavior
/// (opacity levels, always-on-top) and summarizer selection.
///
/// 界面文本全部来自 design/strings.json（<see cref="ApplyStrings"/>），窗口里没有字面量。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    /// <summary>下拉序号 → 语言枚举。顺序即展示顺序，「跟随系统」在首位。</summary>
    private static readonly AppLanguage[] LanguageOrder =
    {
        AppLanguage.System, AppLanguage.ZhHans, AppLanguage.En, AppLanguage.Ja, AppLanguage.Ko,
    };

    /// <summary>
    /// 语言名用**本族语自称**（中文 / English / 日本語 / 한국어），不随界面语言翻译。
    ///
    /// 这是语言选择器的通行做法，也是唯一对用户有用的做法：界面正显示着看不懂的语言时，
    /// 用户要能认出自己那一档。所以这四条**不进文案表**——它们本来就没有"四种译法"。
    /// 只有「跟随系统」是需要翻译的（settings.language.system）。
    /// </summary>
    private static readonly string[] LanguageEndonyms =
    {
        "跟随系统", "中文", "English", "日本語", "한국어",
    };

    /// <summary>打开设置窗时的语言，未保存就关窗时回滚到它（语言是即时生效的）。</summary>
    private readonly AppLanguage _languageOnOpen;

    private bool _loading;
    private bool _saved;

    public SettingsWindow()
    {
        InitializeComponent();
        _languageOnOpen = ParseLanguage(App.Settings.Language);

        // unpackaged 下窗口图标不会自动取 exe 图标，需显式指定（标题栏/任务栏用）。
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico"));
        AppWindow.Resize(new SizeInt32(620, 780));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
        }

        LoadFromSettings();
        BuildLanguageItems();
        ApplyStrings();
        AppStrings.Changed += OnStringsChanged;
        Closed += (_, _) =>
        {
            AppStrings.Changed -= OnStringsChanged;
            // 回滚挂在 Closed 而不是 Cancel_Click：标题栏的 X 不走 Cancel，否则界面会停在
            // 切换后的语言、settings.json 里却是旧值——重启又变回去，是个自相矛盾的状态。
            if (!_saved) ApplyLanguage(_languageOnOpen);
        };
    }

    private void OnStringsChanged() => DispatcherQueue.TryEnqueue(ApplyStrings);

    /// <summary>
    /// 灌入所有界面文本。语言切换后原样重跑一遍——包括下拉项自身，
    /// 「跟随系统」那一条要跟着换语言（其余四条是自称，不变）。
    /// </summary>
    private void ApplyStrings()
    {
        // 版本进标题栏（双端统一：mac 端窗口标题用同一字符串）。
        // 版本来自仓库根 VERSION（csproj 构建期注入 AssemblyVersion）。
        var v = typeof(App).Assembly.GetName().Version;
        Title = AppStrings.F("app.settingsTitle", v?.ToString(3) ?? "?");

        EngineSectionText.Text = AppStrings.S("settings.section.engine");
        EngineCliRadio.Content = AppStrings.S("settings.engine.cli");
        EngineProviderRadio.Content = AppStrings.S("settings.engine.provider");
        EngineRuleRadio.Content = AppStrings.S("settings.engine.rule");
        CliCommandBox.Header = AppStrings.S("settings.cliChoice");
        ProviderModelBox.Header = AppStrings.S("settings.model");

        AppearanceSectionText.Text = AppStrings.S("settings.section.appearance");
        LanguageBox.Header = AppStrings.S("settings.language");
        LanguageNoteText.Text = AppStrings.S("settings.language.note");
        HoverOpacitySlider.Header = AppStrings.S("settings.hoverOpacity");
        IdleOpacitySlider.Header = AppStrings.S("settings.idleOpacity");
        AlwaysOnTopToggle.Header = AppStrings.S("settings.alwaysOnTop");
        AlwaysOnTopToggle.OnContent = AppStrings.S("settings.on");
        AlwaysOnTopToggle.OffContent = AppStrings.S("settings.off");

        DataSectionText.Text = AppStrings.S("settings.section.data");
        SessionSourcesText.Text = AppStrings.S("settings.sessionSources");
        BackfillDaysBox.Header = AppStrings.F("settings.backfillDays", "N");
        SettingsNoteText.Text = AppStrings.S("settings.note");

        CancelButton.Content = AppStrings.S("settings.cancel");
        SaveButton.Content = AppStrings.S("settings.save");

        RefreshSystemLanguageItem();
    }

    /// <summary>
    /// 下拉项只建一次。语言切换时**只改第一条**（「跟随系统」）的文字——其余四条是
    /// 本族语自称，本来就不变。
    ///
    /// 早先是每次 <see cref="ApplyStrings"/> 都 Clear + 重加，而 ApplyStrings 正是由
    /// 这个 ComboBox 的 SelectionChanged 间接触发的：等于在选中回调里把自己整表拆了重建。
    /// 实测后果是设置窗的 UIA 树当场失效（切换后连保存按钮都查不到），真人用鼠标时
    /// 焦点与朗读也会跟着丢。就地改文字既够用又不动结构。
    /// </summary>
    private void BuildLanguageItems()
    {
        _loading = true;
        for (var n = 0; n < LanguageOrder.Length; n++)
        {
            LanguageBox.Items.Add(new ComboBoxItem { Content = LanguageEndonyms[n] });
        }
        var selected = Array.IndexOf(LanguageOrder, ParseLanguage(App.Settings.Language));
        LanguageBox.SelectedIndex = selected >= 0 ? selected : 0;
        _loading = false;
    }

    private void RefreshSystemLanguageItem()
    {
        if (LanguageBox.Items.Count > 0 && LanguageBox.Items[0] is ComboBoxItem first)
        {
            first.Content = AppStrings.S("settings.language.system");
        }
    }

    private static AppLanguage ParseLanguage(string? value) =>
        Enum.TryParse<AppLanguage>(value, out var lang) ? lang : AppLanguage.System;

    /// <summary>
    /// 语言**即时生效**：当场换掉整个应用的文案，不等保存。
    /// 只改运行态，不落盘——落盘在 Save_Click；未保存就关窗由 Closed 回滚。
    /// </summary>
    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedIndex < 0) return;
        ApplyLanguage(LanguageOrder[LanguageBox.SelectedIndex]);
    }

    private static void ApplyLanguage(AppLanguage lang)
    {
        AppStrings.Load(lang);
        // 新生成的摘要跟随当前语言；缓存键也带上它，免得切成英文后重复命令命中旧中文摘要。
        Core.Summarize.SummaryEngine.Locale = AppStrings.Current.Language;
        Core.Summarize.RuleSummarizer.EmptyCommandTitle = AppStrings.S("rule.emptyCommand");
    }

    private void LoadFromSettings()
    {
        _loading = true;
        var s = App.Settings;

        EngineRadios.SelectedIndex = s.Engine switch
        {
            SummaryEngineKind.Cli => 0,
            SummaryEngineKind.Provider => 1,
            _ => 2,
        };
        CliCommandBox.SelectedIndex = s.CliCommand switch
        {
            "claude" => 1,
            "codex" => 2,
            _ => 0,
        };
        ProviderBaseUrlBox.Text = s.ProviderBaseUrl;
        ProviderApiKeyBox.Password = s.ProviderApiKey;
        ProviderModelBox.Text = s.ProviderModel;

        HoverOpacitySlider.Value = s.HoverOpacity;
        IdleOpacitySlider.Value = s.IdleOpacity;
        AlwaysOnTopToggle.IsOn = s.AlwaysOnTop;

        BackfillDaysBox.Value = s.BackfillDays;
        EnableClaudeCheck.IsChecked = s.EnableClaude;
        EnableCodexCheck.IsChecked = s.EnableCodex;
        EnableGrokCheck.IsChecked = s.EnableGrok;
        EnableKimiCheck.IsChecked = s.EnableKimi;
        EnableZcodeCheck.IsChecked = s.EnableZcode;
        // NOTE: AppSettings.ZcodeSessionRoot 仍然生效（空 = 自动探测的默认根），只是不再
        // 出现在 UI 里——默认根实机确认可用，输入框只是噪音。要改仍可编辑 settings.json。

        UpdateEnginePanels();
        _loading = false;
    }

    private void EngineRadios_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateEnginePanels();

    private void UpdateEnginePanels()
    {
        // Guard: SelectionChanged can fire before all named elements are realized.
        if (CliCommandBox is null || ProviderPanel is null) return;
        CliCommandBox.Visibility = EngineRadios.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProviderPanel.Visibility = EngineRadios.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;

        s.Engine = EngineRadios.SelectedIndex switch
        {
            0 => SummaryEngineKind.Cli,
            1 => SummaryEngineKind.Provider,
            _ => SummaryEngineKind.Rule,
        };
        s.CliCommand = (CliCommandBox.SelectedItem as ComboBoxItem)?.Content as string ?? "auto";
        s.ProviderBaseUrl = ProviderBaseUrlBox.Text.Trim();
        s.ProviderApiKey = ProviderApiKeyBox.Password;
        s.ProviderModel = ProviderModelBox.Text.Trim();

        if (LanguageBox.SelectedIndex >= 0)
        {
            s.Language = LanguageOrder[LanguageBox.SelectedIndex].ToString();
        }

        s.HoverOpacity = Math.Round(HoverOpacitySlider.Value, 2);
        s.IdleOpacity = Math.Round(IdleOpacitySlider.Value, 2);
        s.AlwaysOnTop = AlwaysOnTopToggle.IsOn;

        s.BackfillDays = double.IsNaN(BackfillDaysBox.Value) ? 7 : (int)BackfillDaysBox.Value;
        s.EnableClaude = EnableClaudeCheck.IsChecked == true;
        s.EnableCodex = EnableCodexCheck.IsChecked == true;
        s.EnableGrok = EnableGrokCheck.IsChecked == true;
        s.EnableKimi = EnableKimiCheck.IsChecked == true;
        s.EnableZcode = EnableZcodeCheck.IsChecked == true;

        // 无 UI 的字段（ZcodeSessionRoot，README 教用户手改 settings.json）保存前
        // 从**磁盘**重读：内存里的 s 是启动时加载的快照，app 开着时用户手改了文件，
        // 这里一保存就会用旧值静默盖回去（实机审计确认）。
        var onDisk = AppSettings.Load();
        s.ZcodeSessionRoot = onDisk.ZcodeSessionRoot;

        _saved = true;
        s.Save();
        App.Engine.ReloadSummarizer();
        // W1：换了引擎/模型/端点后，之前因旧配置失败到上限的节点应获得新机会。
        App.Coordinator.ResetSummaryAttemptsAndRetry();
        App.MainWindowInstance?.ApplyWindowSettings();
        // NOTE (scaffold): watcher roots / agent toggles take full effect after app restart.

        Close();
    }

    /// <summary>取消：语言的回滚统一在 Closed 里做（X 关窗也要回滚），这里只关。</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
