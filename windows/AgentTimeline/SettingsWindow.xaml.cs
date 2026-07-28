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
/// </summary>
public sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        // 版本进标题栏（双端统一：mac 端窗口标题用同一字符串）。
        // 版本来自仓库根 VERSION（csproj 构建期注入 AssemblyVersion）。
        var v = typeof(App).Assembly.GetName().Version;
        Title = $"Agent Timeline 设置 · v{v?.ToString(3) ?? "?"}";
        // unpackaged 下窗口图标不会自动取 exe 图标，需显式指定（标题栏/任务栏用）。
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico"));
        AppWindow.Resize(new SizeInt32(600, 700));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
        }

        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
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

        s.Save();
        App.Engine.ReloadSummarizer();
        // W1：换了引擎/模型/端点后，之前因旧配置失败到上限的节点应获得新机会。
        App.Coordinator.ResetSummaryAttemptsAndRetry();
        App.MainWindowInstance?.ApplyWindowSettings();
        // NOTE (scaffold): watcher roots / agent toggles take full effect after app restart.

        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
