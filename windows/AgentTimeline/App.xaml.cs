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

        // Start watching only after the window subscribed to coordinator events,
        // so backfill nodes stream into the visible timeline.
        Coordinator.Start();
        Coordinator.RetryPendingSummaries();
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
        Current.Exit();
    }
}
