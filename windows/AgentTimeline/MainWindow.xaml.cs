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
using Microsoft.UI.Xaml.Controls.Primitives; // FlyoutPlacementMode
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
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

    // Acrylic backdrop plumbing (kept alive for the window's lifetime).
    private WindowsSystemDispatcherQueueHelper? _wsdqHelper;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;

    public MainWindow()
    {
        ViewModel = new TimelineViewModel(App.Store, App.Registry, App.Tokens);
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

        App.Coordinator.NodeAdded += node =>
            DispatcherQueue.TryEnqueue(() => ViewModel.OnNodeAdded(node));
        App.Coordinator.NodeSummaryUpdated += (id, summary) =>
            DispatcherQueue.TryEnqueue(() => ViewModel.OnSummaryUpdated(id, summary));
        App.Coordinator.NodeResultLineUpdated += (id, line) =>
            DispatcherQueue.TryEnqueue(() => ViewModel.OnResultLineUpdated(id, line));
        // Dictionary rows changed (new definition / status advance) → refresh chip badges.
        App.Coordinator.CodenamesChanged += () =>
            DispatcherQueue.TryEnqueue(() => ViewModel.RefreshCodenameStatuses());

        // Tray icon must be explicitly created for unpackaged WinUI apps.
        // enablesEfficiencyMode 默认 true 会把整个进程打入 Win11 效率模式（EcoQoS 降频
        // 调度），且本工程显隐走 AppWindow.Show/Hide、永远不会解除——监听/摘要/动画全程
        // 被降速，必须显式关掉。
        TrayIcon.ForceCreate(enablesEfficiencyMode: false);
        TrayIcon.LeftClickCommand = new RelayCommand(TogglePanelVisible);
        TrayAlwaysOnTopItem.IsChecked = App.Settings.AlwaysOnTop;
        WireTrayCommands();

        // 头部过滤菜单也是面板内弹层，参与 hover 抑制（见 RegisterPanelFlyout）。
        if (ProjectFilterButton.Flyout is { } pf) RegisterPanelFlyout(pf);
        if (KindFilterButton.Flyout is { } kf) RegisterPanelFlyout(kf);
    }

    // ─────────────────────────── 面板内弹层与透明度的协奏（P1 实机反馈修复）

    /// <summary>打开中的面板内弹层数：>0 时抑制 idle 降透明。</summary>
    private int _openPanelFlyouts;

    /// <summary>
    /// 面板内弹层（chip 详情、词典、右键菜单、过滤菜单）是独立窗口化 popup：打开即夺走
    /// 激活、指针移入即触发 PointerExited——两条路径都会把主窗降到 idle 0.25，用户面对
    /// 的是浮层悬在一块近透明面板上（实机反馈）。弹层打开期间钉在 hover 不透明度，
    /// 关闭且指针不在面板内时再回落。
    /// </summary>
    private void RegisterPanelFlyout(FlyoutBase flyout)
    {
        flyout.Opened += (_, _) =>
        {
            _openPanelFlyouts++;
            _opacityAnimator?.AnimateTo(App.Settings.HoverOpacity);
        };
        flyout.Closed += (_, _) =>
        {
            _openPanelFlyouts = Math.Max(0, _openPanelFlyouts - 1);
            if (_openPanelFlyouts == 0 && !_pointerOver)
            {
                _opacityAnimator?.AnimateTo(App.Settings.IdleOpacity);
            }
        };
    }

    // ═══════════════════════════════════════════════════ window chrome

    private void InitializeWindowChrome()
    {
        var appWindow = AppWindow;
        appWindow.IsShownInSwitchers = false; // widget mode: tray is the entry point
        // unpackaged 下窗口图标不会自动取 exe 图标（Alt-Tab 关闭但个别系统面板仍会展示）。
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico"));

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
        // Token sizes are logical px；AppWindow 吃物理像素 → 默认尺寸乘 DPI 缩放
        // （用户保存的尺寸已是物理像素，原样恢复）。
        var scale = WindowInterop.GetWindowScale(_hwnd);
        var width = s.WindowWidth > 0 ? s.WindowWidth : (int)Math.Round(t.PanelDefaultWidth * scale);
        var height = s.WindowHeight > 0 ? s.WindowHeight : (int)Math.Round(t.PanelDefaultHeight * scale);

        int x = 0, y = 0;
        var restored = s.WindowX != int.MinValue && s.WindowY != int.MinValue;
        if (restored)
        {
            x = s.WindowX;
            y = s.WindowY;
            // 记忆坐标可能已随显示器拔除/分辨率变化落在所有工作区之外；挂件又没有
            // Alt-Tab/任务栏入口可救援 → 校验矩形与任一显示区相交，否则回退首启位。
            var probe = new RectInt32(x, y, width, height);
            if (DisplayArea.GetFromRect(probe, DisplayAreaFallback.None) is null) restored = false;
        }
        if (!restored)
        {
            // First run (or stale bounds): top-right corner of the primary work area.
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
        // min/max tokens 同为逻辑像素 → 按当前 DPI 换算成物理像素再钳制。
        var scale = WindowInterop.GetWindowScale(_hwnd);
        var clamped = Math.Clamp(size.Width,
            (int)Math.Round(t.PanelMinWidth * scale),
            (int)Math.Round(t.PanelMaxWidth * scale));
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
        try { _settingsWindow?.Close(); } catch { /* already closed */ }
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

        // IsInputActive keeps the acrylic material rendered even while the
        // panel is not the foreground window (the widget is usually unfocused).
        _backdropConfig = new SystemBackdropConfiguration
        {
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
        if (_openPanelFlyouts > 0) return; // 指针移入自家弹层不算离开（P1 实机反馈）
        _opacityAnimator?.AnimateTo(App.Settings.IdleOpacity);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        // 不跟随激活态改写 _backdropConfig.IsInputActive：挂件常态就是失焦，置 false 会让
        // DesktopAcrylic 塌成不透明的 inactive fallback 纯色，透光观感（PRD F5）整个丢失
        // ——构造时的 IsInputActive=true 即为设计意图（TrySetAcrylicBackdrop 注释）。
        var active = args.WindowActivationState != WindowActivationState.Deactivated;
        if (!active && !_pointerOver && _openPanelFlyouts == 0) // 弹层夺走激活不算失焦（P1）
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

    // ─────────────────────────── ledger entry interactions (PRD §3.2b)

    /// <summary>Honors the system "animation effects" setting; resolved once at startup.</summary>
    private static readonly bool AnimationsEnabled = ReadAnimationsEnabled();

    private static bool ReadAnimationsEnabled()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 可划选文本上的单击也切换展开（实机反馈：气泡大面积是文本，原设计只有背景/元信息行
    /// /小 chevron 可点，内容被截断时用户「没有交互能看全」）。划选仍然优先：拖选后存在
    /// 选区则不动；DataContext 沿视觉树上溯（展开态关键点项的 DataContext 是 string）。
    /// </summary>
    private void EntryText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is TextBlock tb && !string.IsNullOrEmpty(tb.SelectedText)) return;
        var el = sender as DependencyObject;
        while (el is not null)
        {
            if (el is FrameworkElement { DataContext: NodeViewModel vm })
            {
                vm.IsExpanded = !vm.IsExpanded;
                e.Handled = true;
                return;
            }
            el = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(el);
        }
    }

    /// <summary>Whole-entry click (background / meta row only — selectable text sits above).</summary>
    private void EntryBackground_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NodeViewModel vm)
        {
            vm.IsExpanded = !vm.IsExpanded;
        }
    }

    private void Entry_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement root || root.DataContext is not NodeViewModel vm) return;
        vm.IsHovering = true;
        if (AnimationsEnabled)
        {
            FadeIn(root.FindName("HoverLayer") as UIElement);
            FadeIn(root.FindName("EntryCopyButton") as UIElement);
        }
    }

    private void Entry_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NodeViewModel vm)
        {
            vm.IsHovering = false;
        }
    }

    /// <summary>Opacity fade-in over opacity.hoverFadeMs (opacity-only, per the motion rules).</summary>
    private static void FadeIn(UIElement? element)
    {
        if (element is null) return;
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(App.Tokens.HoverFadeMs)),
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    /// <summary>Copies the raw command; the button morphs to a ✓ receipt for 800ms.</summary>
    private async void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NodeViewModel vm) return;
        CopyToClipboard(vm.PromptText);
        if (vm.IsCopied) return; // receipt already showing; keep it simple
        vm.IsCopied = true;
        await System.Threading.Tasks.Task.Delay(App.Tokens.CopyMorphMs);
        vm.IsCopied = false;
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard copy failed", ex);
        }
    }

    /// <summary>右键菜单: 复制原话 / 复制摘要 / 跳转到定义节点 / 只看此项目.</summary>
    private void Entry_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement root || root.DataContext is not NodeViewModel vm) return;

        var menu = new MenuFlyout();
        RegisterPanelFlyout(menu);

        var copyRaw = new MenuFlyoutItem { Text = "复制原话" };
        copyRaw.Click += (_, _) => CopyToClipboard(vm.PromptText);
        menu.Items.Add(copyRaw);

        var copySummary = new MenuFlyoutItem { Text = "复制摘要" };
        copySummary.Click += (_, _) => CopyToClipboard(vm.SummaryClipboardText);
        menu.Items.Add(copySummary);

        if (vm.FirstChipName is { } chipName && App.Registry.Lookup(chipName) is { } entry)
        {
            var jump = new MenuFlyoutItem { Text = $"跳转到 {chipName} 定义节点" };
            jump.Click += (_, _) => JumpToNode(entry.DefiningNodeId);
            menu.Items.Add(jump);
        }

        var filterProject = new MenuFlyoutItem { Text = "只看此项目" };
        filterProject.Click += (_, _) => ApplyProjectFilter(vm.Project);
        menu.Items.Add(filterProject);

        menu.ShowAt(root, e.GetPosition(root));
        e.Handled = true;
    }

    // ─────────────────────────── sticky day header (pinned sections)

    private void TimelineScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateStickyDayHeader();
        // 跳跃式滚动（快速甩动/程序化跳转）时 ItemsRepeater 的再实现化发生在随后的布局拍，
        // 本拍读到的是过期几何，粘性条会以错误状态冻结（实机 M3 复现：跳转后常驻或消失）。
        // 布局队列排空后再校准一次。
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateStickyDayHeader);
    }

    /// <summary>
    /// Emulates mac's pinned section headers: shows the day label of the group whose
    /// in-flow header has scrolled above the viewport top. Only realized (non-virtualized)
    /// elements can be measured, which is exactly the set near the viewport.
    /// </summary>
    private void UpdateStickyDayHeader()
    {
        try
        {
            var topmostIndex = -1;
            var topmostY = double.MaxValue;
            var childCount = VisualTreeHelper.GetChildrenCount(NodeRepeater);
            for (var i = 0; i < childCount; i++)
            {
                if (VisualTreeHelper.GetChild(NodeRepeater, i) is not UIElement child) continue;
                var index = NodeRepeater.GetElementIndex(child);
                if (index < 0) continue;
                var y = child.TransformToVisual(TimelineScroller)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                var height = (child as FrameworkElement)?.ActualHeight ?? 0;
                // The element spanning (or first below) the viewport top edge.
                if (y + height > 0 && y < topmostY)
                {
                    topmostY = y;
                    topmostIndex = index;
                }
            }
            if (topmostIndex < 0)
            {
                StickyDayHeader.Visibility = Visibility.Collapsed;
                return;
            }

            // Nearest day header at or above the topmost visible item.
            DayHeaderViewModel? header = null;
            for (var i = Math.Min(topmostIndex, ViewModel.Items.Count - 1); i >= 0; i--)
            {
                if (ViewModel.Items[i] is DayHeaderViewModel h)
                {
                    header = h;
                    break;
                }
            }
            // Suppress while that header itself is still fully visible below the top edge.
            if (header is null ||
                (ViewModel.Items[topmostIndex] is DayHeaderViewModel && topmostY >= 0))
            {
                StickyDayHeader.Visibility = Visibility.Collapsed;
                return;
            }
            StickyDayLabel.Text = header.Label;
            StickyDayHeader.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Warn($"Sticky day header update failed: {ex.Message}");
            StickyDayHeader.Visibility = Visibility.Collapsed;
        }
    }

    private string _currentProjectOption = TimelineViewModel.AllProjects;
    private string _currentKindOption = TimelineViewModel.AllKinds;

    private void ProjectFilterFlyout_Opening(object sender, object e)
    {
        if (sender is not Flyout flyout) return;

        // 来源标注（实机反馈）：每个项目挂「最近活跃」agent 的双字母色块徽标——
        // 与时间线条目的徽标同一视觉（16px 圆角块 + AgentKind.Monogram）；项目
        // 前前后后换多个 agent 时徽标跟随最近干活的那个（Store 查询已按最近活跃
        // 降序，首个即是）；tooltip 给完整分布（同序，如 "Codex 4341 · zcode 36"）。
        // 每次打开现查（单条 GROUP BY，毫秒级），免得维护缓存失效。
        var breakdown = new Dictionary<string, List<(AgentKind Agent, int Count)>>();
        foreach (var (project, agent, count, _) in App.Store.GetProjectAgentCounts())
        {
            if (!breakdown.TryGetValue(project, out var list))
            {
                breakdown[project] = list = new List<(AgentKind, int)>();
            }
            list.Add((agent, count));
        }

        var panel = new StackPanel { Spacing = 2, MinWidth = 200 };
        foreach (var option in ViewModel.ProjectOptions)
        {
            breakdown.TryGetValue(option, out var agents);

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (agents is { Count: > 0 })
            {
                var badge = new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(4),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(App.Tokens.AgentColor(agents[0].Agent)),
                    Child = new TextBlock
                    {
                        Text = agents[0].Agent.Monogram(),
                        FontSize = 7,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                row.Children.Add(badge);
            }

            var label = new TextBlock
            {
                Text = option,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            if (option == _currentProjectOption)
            {
                var check = new FontIcon
                {
                    Glyph = "", // Accept ✓
                    FontSize = 12,
                    Foreground = new SolidColorBrush(App.Tokens.DualColor("accent")),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(check, 2);
                row.Children.Add(check);
            }

            var item = new Button
            {
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 6, 8, 6),
                CornerRadius = new CornerRadius(4),
            };
            if (agents is { Count: > 0 })
            {
                ToolTipService.SetToolTip(item,
                    string.Join(" · ", agents.Select(a => $"{a.Agent.DisplayName()} {a.Count}")));
            }
            var captured = option;
            item.Click += (_, _) =>
            {
                flyout.Hide();
                ApplyProjectFilter(captured);
            };
            panel.Children.Add(item);
        }

        flyout.Content = new ScrollViewer
        {
            Content = panel,
            MaxHeight = 400,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private void KindFilterFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu) return;
        menu.Items.Clear();
        foreach (var option in ViewModel.KindOptions)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = option,
                GroupName = "kindFilter",
                IsChecked = option == _currentKindOption,
            };
            item.Click += (_, _) => ApplyKindFilter(option);
            menu.Items.Add(item);
        }
    }

    private void ApplyProjectFilter(string option)
    {
        _currentProjectOption = option;
        ProjectFilterLabel.Text = option + " ▾"; // 超出 MaxWidth 由 TextTrimming 省略
        ViewModel.SetProjectFilter(option);
    }

    private void ApplyKindFilter(string option)
    {
        _currentKindOption = option;
        KindFilterLabel.Text = option + " ▾";
        ViewModel.SetKindFilter(option);
    }

    private void LoadMore_Click(object sender, RoutedEventArgs e) => ViewModel.LoadMore();

    private void HidePanel_Click(object sender, RoutedEventArgs e)
    {
        SaveWindowBounds();
        AppWindow.Hide();
    }

    // ──────────────────────────── codename chips + dictionary (F3 + 生命周期)

    private void CodenameChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button chip || chip.DataContext is not CodenameChipViewModel chipVm) return;
        var entry = App.Registry.Lookup(chipVm.Name);

        var panel = new StackPanel { Spacing = 6, MaxWidth = 320 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new TextBlock
        {
            Text = chipVm.Name,
            FontFamily = new FontFamily("Cascadia Code"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            IsTextSelectionEnabled = true,
        });
        if (entry is not null && entry.Status.Length > 0)
        {
            header.Children.Add(StatusPill(entry));
        }
        panel.Children.Add(header);

        if (entry is null)
        {
            panel.Children.Add(SecondaryText("尚未登记"));
            var quick = new Flyout { Content = panel };
            RegisterPanelFlyout(quick);
            quick.ShowAt(chip);
            return;
        }

        panel.Children.Add(string.IsNullOrEmpty(entry.Definition)
            ? SecondaryText("暂无定义（等待摘要提炼或定义式重述）")
            : new TextBlock
            {
                Text = entry.Definition,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });

        if (entry.LastContext.Length > 0)
        {
            panel.Children.Add(SecondaryText($"最近提及：…{entry.LastContext}…"));
        }
        panel.Children.Add(SecondaryText(MetaLine(entry)));

        var flyout = new Flyout { Content = panel };
        RegisterPanelFlyout(flyout);
        var jump = new Button { Content = "跳转到定义节点", Margin = new Thickness(0, 4, 0, 0) };
        jump.Click += (_, _) =>
        {
            flyout.Hide();
            JumpToNode(entry.DefiningNodeId);
        };
        panel.Children.Add(jump);
        flyout.ShowAt(chip);
    }

    /// <summary>
    /// The dictionary panel (PRD §3.3b 词典总览入口): all registered codes, most recently
    /// updated first — 代号 + 状态 + 定义 + 最近提及, click → jump to the defining node.
    /// </summary>
    private void OpenDictionary_Click(object sender, RoutedEventArgs e)
    {
        var entries = App.Registry.All();
        var root = new StackPanel { Spacing = 6, MinWidth = 280, MaxWidth = 340 };
        root.Children.Add(new TextBlock
        {
            Text = $"代号词典（{entries.Count}）",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        var flyout = new Flyout { Content = root, Placement = FlyoutPlacementMode.Bottom };
        RegisterPanelFlyout(flyout);
        if (entries.Count == 0)
        {
            root.Children.Add(SecondaryText(
                "尚无登记的代号 — 会话中出现 \"N1: xxx\" 式定义或 REQ-3 式长代号后会自动登记"));
        }
        else
        {
            var list = new StackPanel { Spacing = 2 };
            foreach (var entry in entries)
            {
                list.Children.Add(DictionaryRow(entry, flyout));
            }
            root.Children.Add(new ScrollViewer
            {
                Content = list,
                MaxHeight = 380,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });
        }
        flyout.ShowAt(DictionaryButton);
    }

    private Button DictionaryRow(Core.CodenameEntry entry, Flyout flyout)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontFamily = new FontFamily("Cascadia Code"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(App.Tokens.DualColor("codenameChipText")),
        });
        if (entry.Status.Length > 0)
        {
            header.Children.Add(StatusPill(entry));
        }
        header.Children.Add(new TextBlock
        {
            Text = (entry.Updated ?? entry.FirstSeen).ToLocalTime().ToString("MM-dd HH:mm"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(App.Tokens.DualColor("textTertiary")),
        });

        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(header);
        content.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(entry.Definition) ? "（暂无定义）" : entry.Definition,
            FontSize = 10.5,
            Opacity = string.IsNullOrEmpty(entry.Definition) ? 0.5 : 0.8,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (entry.LastContext.Length > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"…{entry.LastContext}…",
                FontSize = 10,
                Opacity = 0.55,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            });
        }

        var row = new Button
        {
            Content = content,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4, 6, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        row.Click += (_, _) =>
        {
            flyout.Hide();
            JumpToNode(entry.DefiningNodeId);
        };
        return row;
    }

    /// <summary>Colored status label pill (完成/变更/进行中/定义), mac chip-popover style.</summary>
    private static Border StatusPill(Core.CodenameEntry entry)
    {
        var color = App.Tokens.StatusColor(entry.StatusValue);
        return new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x24, color.R, color.G, color.B)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = entry.Status,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = new SolidColorBrush(color),
            },
        };
    }

    private static TextBlock SecondaryText(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        Opacity = 0.7,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    private static string MetaLine(Core.CodenameEntry entry)
    {
        var line = $"首次 {entry.FirstSeen.ToLocalTime():yyyy-MM-dd HH:mm} · 共 {entry.Occurrences} 次";
        if (entry.Updated is { } updated)
        {
            line += $" · 更新 {updated.ToLocalTime():MM-dd HH:mm}";
        }
        return line;
    }

    /// <summary>
    /// Jump to a node: clear filters when they hide it (mirrors mac jumpToDefinition),
    /// page in older history when it lies beyond the loaded window, then scroll + expand.
    /// </summary>
    private void JumpToNode(long nodeId)
    {
        if (ViewModel.FindById(nodeId) is null && ViewModel.HasActiveFilters)
        {
            // 复位过滤按钮标签（VM 侧一次 ClearFilters 完成重载，不走 Apply* 以免双重加载）。
            _currentProjectOption = TimelineViewModel.AllProjects;
            _currentKindOption = TimelineViewModel.AllKinds;
            ProjectFilterLabel.Text = TimelineViewModel.AllProjects + " ▾";
            KindFilterLabel.Text = TimelineViewModel.AllKinds + " ▾";
            ViewModel.ClearFilters();
        }
        if (ViewModel.FindById(nodeId) is null && !ViewModel.EnsureLoaded(nodeId)) return;
        ScrollToNode(nodeId);
    }

    private void ScrollToNode(long nodeId)
    {
        var vm = ViewModel.FindById(nodeId);
        if (vm is null) return; // still not materialized (e.g. beyond the paging guard)
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

    /// <summary>
    /// 托盘菜单走 Command 而非 Click —— 原因见 MainWindow.xaml 里 MenuFlyout 上的注释。
    /// </summary>
    private void WireTrayCommands()
    {
        TrayShowHideItem.Command = new RelayCommand(TogglePanelVisible);
        TraySettingsItem.Command = new RelayCommand(OpenSettings);
        TrayExitItem.Command = new RelayCommand(App.Shutdown);
        TrayAlwaysOnTopItem.Command = new RelayCommand(() =>
        {
            // 取反的基准是 App.Settings 而不是 IsChecked：原生菜单只把 IsChecked 读出去
            // 画勾（库里是 PopupMenuItem.Checked = toggleItem.IsChecked 的单向），不回写，
            // 读 IsChecked 会永远取到旧值、开关一次也翻不动。回写 IsChecked 是为了下次
            // 打开菜单时勾选态正确。
            var next = !App.Settings.AlwaysOnTop;
            App.Settings.AlwaysOnTop = next;
            TrayAlwaysOnTopItem.IsChecked = next;
            App.Settings.Save();
            ApplyWindowSettings();
        });
    }
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
