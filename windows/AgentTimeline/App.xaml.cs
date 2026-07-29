using AgentTimeline.Core;
using AgentTimeline.Core.Summarize;
using Microsoft.UI.Xaml;

namespace AgentTimeline;

/// <summary>
/// Composition root — assembles the module graph described in docs/ARCHITECTURE.md
/// (settings → store → registry → engine → coordinator → window) and owns lifetimes.
/// </summary>
public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = null!;
    public static DesignTokens Tokens { get; private set; } = null!;
    public static Store Store { get; private set; } = null!;
    public static CodenameRegistry Registry { get; private set; } = null!;
    public static SummaryEngine Engine { get; private set; } = null!;
    public static TimelineCoordinator Coordinator { get; private set; } = null!;
    public static MainWindow? MainWindowInstance { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled exception", e.Exception);
            e.Handled = true; // widget should not vanish on a stray exception
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppPaths.EnsureDataDirs();
        Settings = AppSettings.Load();
        Tokens = DesignTokens.Load();
        // 文案表要在任何界面构建之前载入：托盘菜单与代码构建的弹层都在 MainWindow
        // 构造期取文案，晚一步就会拿到键名。
        AppStrings.Load(Enum.TryParse<AppLanguage>(Settings.Language, out var lang)
            ? lang
            : AppLanguage.System);
        // Core 不该反过来依赖应用层，故解析结果由这里推给它；摘要 prompt 语言与
        // 摘要缓存键都取这个值（见 SummaryEngine.CacheKey）。
        Core.Summarize.SummaryEngine.Locale = AppStrings.Current.Language;
        Core.Summarize.RuleSummarizer.EmptyCommandTitle = AppStrings.S("rule.emptyCommand");
        // Code-built UI (chip badges, flyouts) picks dual-token variants by app theme.
        Tokens.DarkTheme = RequestedTheme == ApplicationTheme.Dark;

        // First run: seed opacity settings from the design tokens (afterwards 设置 owns them).
        if (!File.Exists(AppPaths.SettingsFile))
        {
            Settings.HoverOpacity = Tokens.HoverOpacity;
            Settings.IdleOpacity = Tokens.IdleOpacity;
            Settings.Save();
        }

        Store = new Store(AppPaths.DatabaseFile);
        Registry = new CodenameRegistry(Store);
        Engine = new SummaryEngine(Settings);
        Coordinator = new TimelineCoordinator(Store, Registry, Engine, Settings);

        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();

        // One-time per replay version: rebuild the codename dictionary from stored history
        // (PRD §3.3, marker persisted only after completion). The watcher and summary
        // engine start only from the completion callback, so replay and watcher never
        // write the codenames table concurrently — and the window has already subscribed
        // to coordinator events, so backfill nodes stream into the visible timeline.
        Coordinator.ReplayCodenamesIfNeeded(() =>
        {
            Coordinator.Start();
            Coordinator.RetryPendingSummaries();
        });
    }

    /// <summary>Full teardown; invoked from the tray menu 退出.</summary>
    public static void Shutdown()
    {
        try
        {
            MainWindowInstance?.PrepareForExit(); // saves bounds, allows close, disposes tray icon
            Settings.Save();
            Coordinator.Dispose();
            Engine.Dispose();
            Store.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Error during shutdown", ex);
        }
        // WinUI 3 已知缺陷（microsoft-ui-xaml #5931）：无「打开且激活」窗口时 Exit() 不生效，
        // 而托盘退出的典型场景恰是主窗已隐藏——先显式 Close 主窗（_allowClose 已置位），
        // 再 Exit；托盘图标已 Dispose，若仍残留进程用户将无任何入口，Environment.Exit 兜底。
        try { MainWindowInstance?.Close(); }
        catch (Exception ex) { Log.Error("Error closing main window on exit", ex); }
        Current.Exit();
        Environment.Exit(0);
    }
}
