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
        Title = "Agent Timeline 设置";
        AppWindow.Resize(new SizeInt32(600, 760));
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
        EnableKimiCheck.IsChecked = s.EnableKimi;
        EnableZcodeCheck.IsChecked = s.EnableZcode;
        ZcodeRootBox.Text = s.ZcodeSessionRoot;

        UpdateEnginePanels();
    }

    private void EngineRadios_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateEnginePanels();

    private void UpdateEnginePanels()
    {
        // Guard: SelectionChanged can fire before all named panels are realized.
        if (CliPanel is null || ProviderPanel is null) return;
        CliPanel.Visibility = EngineRadios.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        s.EnableKimi = EnableKimiCheck.IsChecked == true;
        s.EnableZcode = EnableZcodeCheck.IsChecked == true;
        s.ZcodeSessionRoot = ZcodeRootBox.Text.Trim();

        s.Save();
        App.Engine.ReloadSummarizer();
        App.MainWindowInstance?.ApplyWindowSettings();
        // NOTE (scaffold): watcher roots / agent toggles take full effect after app restart.

        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
