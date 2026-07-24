using System.Runtime.InteropServices;
using System.Windows.Input;
using AgentTimeline.Core;
using AgentTimeline.Interop;
using AgentTimeline.UI;
using H.NotifyIcon;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace AgentTimeline;

/// <summary>
/// The floating timeline panel (PRD F5):
///   - borderless resizable window (AppWindow + OverlappedPresenter, thin border kept for
///     resize grips, caption removed);
///   - DesktopAcrylicController translucency;
///   - hover ⇒ opacity.hover (0.95), exit/deactivate ⇒ opacity.idle (0.25),
///     animated over opacity.transitionMs via OpacityAnimator (layered-window alpha);
///   - system tray icon (H.NotifyIcon) with 显示/隐藏 · 总在最前 · 设置 · 退出;
///   - close button / Alt+F4 hides to tray instead of exiting.
/// </summary>
public sealed partial class MainWindow : Window
{
    public TimelineViewModel ViewModel { get; }

    private readonly IntPtr _hwnd;
    private OverlappedPresenter? _presenter;
    private OpacityAnimator? _opacityAnimator;
    private SettingsWindow? _settingsWindow;
    private bool _pointerOver;
    private bool _allowClose;
    private bool _clampingSize;
    private bool _filterReady;

    // Acrylic backdrop plumbing (kept alive for the window's lifetime).
    private WindowsSystemDispatcherQueueHelper? _wsdqHelper;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;

    public MainWindow()
    {
        ViewModel = new TimelineViewModel(App.Store, App.Tokens);
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        Title = "Agent Timeline";

        InitializeWindowChrome();
        TrySetAcrylicBackdrop();

        _opacityAnimator = new OpacityAnimator(
            _hwnd, RootGrid, DispatcherQueue, App.Tokens.TransitionMs);
        _opacityAnimator.SetImmediate(App.Settings.HoverOpacity); // starts focused

        Activated += OnWindowActivated;
        Closed += OnWindowClosed;

        // Timeline data: initial page from the store, then live coordinator events
        // (raised on background threads → marshalled onto this window's DispatcherQueue).
        ViewModel.LoadInitial();
        ProjectFilter.SelectedIndex = 0;
        _filterReady = true;

        App.Coordinator.NodeAdded += node =>
            DispatcherQueue.TryEnqueue(() => ViewModel.OnNodeAdded(node));
        App.Coordinator.NodeSummaryUpdated += (id, summary) =>
            DispatcherQueue.TryEnqueue(() => ViewModel.OnSummaryUpdated(id, summary));
        App.Coordinator.NodeResultLineUpdated += (id, line) =>
            DispatcherQueue.TryEnqueue(() => ViewModel.OnResultLineUpdated(id, line));

        // Tray icon must be explicitly created for unpackaged WinUI apps.
        TrayIcon.ForceCreate();
        TrayIcon.LeftClickCommand = new RelayCommand(TogglePanelVisible);
        TrayAlwaysOnTopItem.IsChecked = App.Settings.AlwaysOnTop;
    }

    // ═══════════════════════════════════════════════════ window chrome

    private void InitializeWindowChrome()
    {
        var appWindow = AppWindow;
        appWindow.IsShownInSwitchers = false; // widget mode: tray is the entry point

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            _presenter = presenter;
            // (hasBorder: true, hasTitleBar: false) keeps the thin resize border while
            // removing the caption — a fully borderless presenter would lose resizing.
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = App.Settings.AlwaysOnTop;
        }

        RestoreWindowBounds();
        appWindow.Closing += OnAppWindowClosing;
        appWindow.Changed += OnAppWindowChanged;
    }

    private void RestoreWindowBounds()
    {
        var s = App.Settings;
        var t = App.Tokens;
        // Token sizes are logical px; treated as physical px here (scaffold simplification —
        // multiply by the window's rasterization scale if exact DPI fidelity matters).
        var width = s.WindowWidth > 0 ? s.WindowWidth : t.PanelDefaultWidth;
        var height = s.WindowHeight > 0 ? s.WindowHeight : t.PanelDefaultHeight;

        int x, y;
        if (s.WindowX != int.MinValue && s.WindowY != int.MinValue)
        {
            x = s.WindowX;
            y = s.WindowY;
        }
        else
        {
            // First run: top-right corner of the primary work area.
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            x = area.X + area.Width - width - 24;
            y = area.Y + 24;
        }
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    public void SaveWindowBounds()
    {
        try
        {
            var s = App.Settings;
            s.WindowX = AppWindow.Position.X;
            s.WindowY = AppWindow.Position.Y;
            s.WindowWidth = AppWindow.Size.Width;
            s.WindowHeight = AppWindow.Size.Height;
            s.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save window bounds", ex);
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        // Close button / Alt+F4 → hide to tray; 退出 lives in the tray menu.
        args.Cancel = true;
        SaveWindowBounds();
        sender.Hide();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || _clampingSize) return;
        var t = App.Tokens;
        var size = sender.Size;
        var clamped = Math.Clamp(size.Width, t.PanelMinWidth, t.PanelMaxWidth);
        if (clamped == size.Width) return;
        _clampingSize = true;
        try { sender.Resize(new SizeInt32(clamped, size.Height)); }
        finally { _clampingSize = false; }
    }

    /// <summary>Called by App.Shutdown before Application.Exit.</summary>
    public void PrepareForExit()
    {
        _allowClose = true;
        SaveWindowBounds();
        TrayIcon.Dispose();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;
    }

    // ═══════════════════════════════════════════════ acrylic backdrop

    /// <summary>
    /// Manual DesktopAcrylicController setup (standard Windows App SDK pattern) —
    /// gives us a handle to tweak tint/fallback later, unlike the one-line
    /// Window.SystemBackdrop convenience property.
    /// </summary>
    private void TrySetAcrylicBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            Log.Warn("DesktopAcrylic not supported on this OS; panel stays opaque");
            return;
        }

        _wsdqHelper = new WindowsSystemDispatcherQueueHelper();
        _wsdqHelper.EnsureWindowsSystemDispatcherQueueController();

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsAlwaysActive = false,
            IsInputActive = true,
        };
        SyncBackdropTheme();

        _acrylicController = new DesktopAcrylicController();
        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);

        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (_, _) => SyncBackdropTheme();
        }
    }

    private void SyncBackdropTheme()
    {
        if (_backdropConfig is null || Content is not FrameworkElement root) return;
        _backdropConfig.Theme = root.ActualTheme switch
        {
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            ElementTheme.Light => SystemBackdropTheme.Light,
            _ => SystemBackdropTheme.Default,
        };
    }

    // ═══════════════════════════════════════ hover / focus opacity (F5)

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = true;
        _opacityAnimator?.AnimateTo(App.Settings.HoverOpacity);
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = false;
        _opacityAnimator?.AnimateTo(App.Settings.IdleOpacity);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        var active = args.WindowActivationState != WindowActivationState.Deactivated;
        if (_backdropConfig is not null) _backdropConfig.IsInputActive = active;
        if (!active && !_pointerOver)
        {
            _opacityAnimator?.AnimateTo(App.Settings.IdleOpacity);
        }
    }

    /// <summary>Re-applies settings that affect the window (called from SettingsWindow after 保存).</summary>
    public void ApplyWindowSettings()
    {
        if (_presenter is not null) _presenter.IsAlwaysOnTop = App.Settings.AlwaysOnTop;
        TrayAlwaysOnTopItem.IsChecked = App.Settings.AlwaysOnTop;
        _opacityAnimator?.AnimateTo(_pointerOver ? App.Settings.HoverOpacity : App.Settings.IdleOpacity);
    }

    // ═══════════════════════════════════════════════════ interactions

    private void HeaderBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Borderless window: dragging via the Win32 caption trick (see WindowInterop).
        var point = e.GetCurrentPoint(HeaderBar);
        if (point.Properties.IsLeftButtonPressed)
        {
            WindowInterop.BeginWindowDrag(_hwnd);
        }
    }

    private void ExpandNode_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NodeViewModel vm)
        {
            vm.IsExpanded = !vm.IsExpanded;
        }
    }

    private void ProjectFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filterReady || ProjectFilter.SelectedItem is not string option) return;
        ViewModel.SetProjectFilter(option);
    }

    private void LoadMore_Click(object sender, RoutedEventArgs e) => ViewModel.LoadMore();

    private void HidePanel_Click(object sender, RoutedEventArgs e)
    {
        SaveWindowBounds();
        AppWindow.Hide();
    }

    // ─────────────────────────────────────────── codename chips (F3)

    private void CodenameChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button chip || chip.Content is not string name) return;
        var entry = App.Registry.Lookup(name);

        var panel = new StackPanel { Spacing = 4, MaxWidth = 300 };
        panel.Children.Add(new TextBlock
        {
            Text = name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            IsTextSelectionEnabled = true,
        });
        panel.Children.Add(new TextBlock
        {
            Text = entry?.Definition ?? "暂无定义（等待 LLM 提炼，或该代号仅由正则识别）",
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });

        if (entry is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"首次出现：{entry.FirstSeen.ToLocalTime():yyyy-MM-dd HH:mm} · 共出现 {entry.Occurrences} 次",
                FontSize = 10.5,
                Opacity = 0.6,
                IsTextSelectionEnabled = true,
            });
            if (!string.IsNullOrEmpty(entry.ContextExcerpt))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = entry.ContextExcerpt,
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7,
                    IsTextSelectionEnabled = true,
                });
            }

            var flyout = new Flyout { Content = panel };
            var jump = new Button { Content = "跳转到定义节点", Margin = new Thickness(0, 4, 0, 0) };
            jump.Click += (_, _) =>
            {
                flyout.Hide();
                ScrollToNode(entry.DefiningNodeId);
            };
            panel.Children.Add(jump);
            flyout.ShowAt(chip);
        }
        else
        {
            new Flyout { Content = panel }.ShowAt(chip);
        }
    }

    private void ScrollToNode(long nodeId)
    {
        var vm = ViewModel.FindById(nodeId);
        if (vm is null) return; // filtered out or beyond the loaded pages
        var index = ViewModel.IndexOf(vm);
        if (index < 0) return;
        vm.IsExpanded = true;
        var element = NodeRepeater.GetOrCreateElement(index);
        element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.15 });
    }

    // ─────────────────────────────────────────────── settings window

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
    }

    // ──────────────────────────────────────────────────── tray menu

    private void TogglePanelVisible()
    {
        if (AppWindow.IsVisible)
        {
            SaveWindowBounds();
            AppWindow.Hide();
        }
        else
        {
            AppWindow.Show();
            Activate();
        }
    }

    private void TrayShowHide_Click(object sender, RoutedEventArgs e) => TogglePanelVisible();

    private void TrayAlwaysOnTop_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.AlwaysOnTop = TrayAlwaysOnTopItem.IsChecked;
        App.Settings.Save();
        ApplyWindowSettings();
    }

    private void TraySettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void TrayExit_Click(object sender, RoutedEventArgs e) => App.Shutdown();
}

/// <summary>Minimal ICommand for the tray icon's LeftClickCommand.</summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    public RelayCommand(Action execute) => _execute = execute;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
}

/// <summary>
/// Ensures a Windows.System.DispatcherQueue exists on this thread — required by
/// DesktopAcrylicController (standard boilerplate from the Windows App SDK docs).
/// </summary>
internal sealed class WindowsSystemDispatcherQueueHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        internal int dwSize;
        internal int threadType;
        internal int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        [In] DispatcherQueueOptions options,
        [In, Out, MarshalAs(UnmanagedType.IUnknown)] ref object? dispatcherQueueController);

    private object? _dispatcherQueueController;

    public void EnsureWindowsSystemDispatcherQueueController()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null) return;
        if (_dispatcherQueueController is not null) return;

        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,    // DQTYPE_THREAD_CURRENT
            apartmentType = 2, // DQTAT_COM_STA
        };
        CreateDispatcherQueueController(options, ref _dispatcherQueueController);
    }
}
