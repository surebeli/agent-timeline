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
    private bool _filterReady;

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
        ProjectFilter.SelectedIndex = 0;
        KindFilter.SelectedIndex = 0;
        _filterReady = true;

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
        filterProject.Click += (_, _) =>
        {
            // Route through the ComboBox so its display stays in sync with the filter.
            if (ProjectFilter.Items.Contains(vm.Project))
            {
                ProjectFilter.SelectedItem = vm.Project;
            }
        };
        menu.Items.Add(filterProject);

        menu.ShowAt(root, e.GetPosition(root));
        e.Handled = true;
    }

    // ─────────────────────────── sticky day header (pinned sections)

    private void TimelineScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateStickyDayHeader();
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

    private void ProjectFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filterReady || ProjectFilter.SelectedItem is not string option) return;
        ViewModel.SetProjectFilter(option);
    }

    private void KindFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filterReady || KindFilter.SelectedItem is not string option) return;
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
            new Flyout { Content = panel }.ShowAt(chip);
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
            _filterReady = false;
            ProjectFilter.SelectedIndex = 0;
            KindFilter.SelectedIndex = 0;
            _filterReady = true;
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
