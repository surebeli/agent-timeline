// README「Windows 实机一览」拍摄用的窗口 / 输入 / 合成工具。
// 对标 mac 侧 macos/scripts/shots/{window-tool,compose}.swift，两件套在这里合一。
//
//   WindowTool list                        列出 AgentTimeline 的在屏窗口
//   WindowTool dpi                         报每个显示器的 DPI 与缩放
//   WindowTool scale get|set <pct>        读 / 改主显示器缩放（set 会重排所有窗口）
//   WindowTool rect   <AutomationId>       报某个控件的屏幕矩形（诊断用）
//   WindowTool invoke <AutomationId>       UIA 调用一次（用来关掉上一态留下的弹层）
//   WindowTool shot   <hwnd> <out.png>     按窗口抓取（PrintWindow + PW_RENDERFULLCONTENT）
//   WindowTool move   <x> <y>              移动指针（SendInput → SetCursorPos 依次试，读回坐标为准）
//   WindowTool shoot  <前缀> [--invoke <AutomationId>] [--settle ms] [--park x,y]
//                                          UIA 调用 → 前后像素比对校验 → 抓完所有窗口
//   WindowTool compose <out.png> <W> <H> <in.png@x,y> [...]   合成到统一画布
//
// ── 为什么按窗口抓、不按屏幕区域抓
//
// 屏幕区域截图会把**盖在上面的第三方全屏浮层**一起摄进来。mac 端实测有
// UURemoteServer（layer 1000）与另一个 layer 25 的全屏窗口，旧版词典截图上那些
// 彩色光斑就是它们，当时一度被误判成应用的半透明缺陷。本机同样常驻远程工具
// （GameViewer / 网易UU远程）。PrintWindow 取窗口自身的渲染结果，与 z 序、遮挡无关。
//
// ── 三处与 mac 不同的实机事实（都是实测出来的，不是照搬）
//
// 1. **弹层渲染在面板窗口内部**：WinUI 3 桌面端 Flyout 默认受
//    ShouldConstrainToRootBounds 约束——词典弹层实测被系统左移挤回面板内
//    （面板 300..940，弹层 576..916），不像 mac 那样恒定溢出面板右缘 122pt。
//    于是：抓面板一张就够；mac 那条「词典态必须比时间线态宽」的不变式在
//    Windows 上**不成立**，照抄只会误报。compose 仍按并集居中，将来若改成
//    窗口化 popup 也接得住。
// 2. **合成鼠标输入部分被吞**：SendInput 连指针都挪不动（实测 move 前后 GetCursorPos
//    完全不变，DEBUG-PLAYBOOK §2a 记过"合成输入起不动原生 NC 拖拽"），但
//    **SetCursorPos 可用**（2026-07-30 实测）。所以：按钮一律走 UIA InvokePattern、
//    不靠点坐标；挪指针则两条路依次试、以**读回坐标**为准，不看返回值。
// 3. **UIA 树会退化**：应用跑久了、或残留 tooltip 之后，面板窗口的 UIA 后代
//    可能只剩几个节点（实测从 96 掉到 4，按 AutomationId 就找不到控件了）。
//    因此每一态都从**新启动的应用**拍，且判定弹层是否真开**不看 UIA 树**，
//    改用前后像素比对——UIA 只负责"按下按钮"这一件它做得可靠的事。
//
// 判据必须硬：弹层没开时产出的图尺寸完全正常，是最坏的静默失败。
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace AgentTimeline.Shots;

internal static class WindowTool
{
    private const string MainWindowClass = "WinUIDesktopWin32WindowClass";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // 必须 per-monitor v2：否则 GetWindowRect / PrintWindow 拿到的是被系统
        // 虚拟化过的逻辑像素，200% 缩放下会静默产出半尺寸、再被拉伸糊掉的图。
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (args.Length == 0) return Usage();
        try
        {
            switch (args[0])
            {
                case "list": return List();
                case "dpi": return Dpi();
                case "scale": return Scale(args);
                case "rect": return args.Length < 2 ? Usage() : Rect(args[1]);
                case "invoke": return args.Length < 2 ? Usage() : Invoke(args[1]);
                case "border": return Border();
                case "move": return args.Length < 3 ? Usage() : MovePointer(Int(args[1]), Int(args[2]));
                case "shot":
                    return args.Length < 3
                        ? Usage()
                        : Shot(new IntPtr(long.Parse(args[1], CultureInfo.InvariantCulture)), args[2]);
                case "shoot": return args.Length < 2 ? Usage() : Shoot(args[1], args[2..]);
                case "compose":
                    return args.Length < 5 ? Usage() : Compose(args[1], Int(args[2]), Int(args[3]), args[4..]);
                default: return Usage();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"失败: {ex.Message}");
            return 1;
        }
    }

    private static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    private static (int X, int Y) Pair(string s)
    {
        var parts = s.Split(',');
        return (Int(parts[0]), Int(parts[1]));
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "用法:\n" +
            "  WindowTool list\n" +
            "  WindowTool dpi\n" +
            "  WindowTool scale  get | set <pct>\n" +
            "  WindowTool rect   <AutomationId>\n" +
            "  WindowTool invoke <AutomationId>\n" +
            "  WindowTool shot   <hwnd> <out.png>\n" +
            "  WindowTool move   <x> <y>\n" +
            "  WindowTool shoot  <前缀> [--invoke <AutomationId>] [--settle ms] [--park x,y]\n" +
            "  WindowTool compose <out.png> <W> <H> <in.png@x,y> [...]");
        return 2;
    }

    // ═══════════════════════════════════════════════ 窗口枚举 / 诊断

    private static int List()
    {
        foreach (var (hwnd, rect, cls, title) in AppWindows())
        {
            Console.WriteLine($"{hwnd.ToInt64()} {rect.Left} {rect.Top} " +
                              $"{rect.Right - rect.Left} {rect.Bottom - rect.Top} {cls} {title}");
        }
        return 0;
    }

    private static List<(IntPtr Hwnd, RECT Rect, string Class, string Title)> AppWindows()
    {
        var pids = Process.GetProcessesByName("AgentTimeline").Select(p => (uint)p.Id).ToHashSet();
        var found = new List<(IntPtr, RECT, string, string)>();
        if (pids.Count == 0) return found;

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains(pid)) return true;
            if (!IsWindowVisible(hwnd)) return true;
            if (!GetWindowRect(hwnd, out var rect)) return true;
            if (rect.Right - rect.Left <= 1 || rect.Bottom - rect.Top <= 1) return true;  // 0 尺寸宿主窗

            var cls = new StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            var title = new StringBuilder(512);
            GetWindowText(hwnd, title, title.Capacity);
            found.Add((hwnd, rect, cls.ToString(), title.ToString()));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static int Dpi()
    {
        var monitors = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (hMon, _, _, _) => { monitors.Add(hMon); return true; }, IntPtr.Zero);
        foreach (var hMon in monitors)
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(hMon, ref info);
            GetDpiForMonitor(hMon, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _);
            var r = info.rcMonitor;
            Console.WriteLine($"{info.szDevice} {r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top} " +
                              $"dpi={dpiX} scale={dpiX * 100 / 96}%");
        }
        return 0;
    }

    /// <summary>
    /// 读/写主显示器的缩放百分比。<c>scale get</c> 打印当前值；<c>scale set &lt;pct&gt;</c> 改到目标档。
    ///
    /// 改全局缩放会重排屏幕上所有已打开的窗口——这是有意为之、由调用方负责告知用户，
    /// 不是副作用。写完不自动改回：拍摄脚本按用户约定把机器留在目标缩放上。
    /// </summary>
    private static int Scale(string[] args)
    {
        var (adapterId, sourceId) = PrimarySource();
        var get = new DISPLAYCONFIG_SOURCE_DPI_SCALE_GET
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE,
                size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DPI_SCALE_GET>(),
                adapterId = adapterId,
                id = sourceId,
            },
        };
        if (DisplayConfigGetDeviceInfo(ref get) != 0)
        {
            throw new InvalidOperationException("读取显示缩放失败（DisplayConfigGetDeviceInfo）");
        }

        // curScaleRel 是「当前档相对推荐档」的偏移 → 反推推荐档在 DpiSteps 里的下标
        var currentDpi = CurrentPrimaryScale();
        var currentIdx = Array.IndexOf(DpiSteps, currentDpi);
        if (currentIdx < 0)
        {
            throw new InvalidOperationException($"当前缩放 {currentDpi}% 不在已知档位表里");
        }
        var recommendedIdx = currentIdx - get.curScaleRel;

        // 用法两种：`scale` / `scale get` 只读；`scale set <pct>` 才写。
        if (args.Length < 2 || args[1] == "get")
        {
            var min = DpiSteps[Math.Clamp(recommendedIdx + get.minScaleRel, 0, DpiSteps.Length - 1)];
            var max = DpiSteps[Math.Clamp(recommendedIdx + get.maxScaleRel, 0, DpiSteps.Length - 1)];
            Console.WriteLine($"scale={currentDpi}% recommended={DpiSteps[recommendedIdx]}% range={min}%..{max}%");
            return 0;
        }
        if (args[1] != "set" || args.Length < 3)
        {
            throw new ArgumentException("用法：scale get | scale set <百分比>");
        }

        var target = Int(args[2]);
        var targetIdx = Array.IndexOf(DpiSteps, target);
        if (targetIdx < 0)
        {
            throw new ArgumentException($"{target}% 不是合法档位，可选：{string.Join(", ", DpiSteps)}");
        }
        if (target == currentDpi)
        {
            Console.WriteLine($"scale={currentDpi}% 已是目标值，无需改动");
            return 0;
        }

        var rel = targetIdx - recommendedIdx;
        if (rel < get.minScaleRel || rel > get.maxScaleRel)
        {
            throw new ArgumentException(
                $"{target}% 超出本显示器允许范围（相对档位 {rel} 不在 {get.minScaleRel}..{get.maxScaleRel}）");
        }

        var set = new DISPLAYCONFIG_SOURCE_DPI_SCALE_SET
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE,
                size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DPI_SCALE_SET>(),
                adapterId = adapterId,
                id = sourceId,
            },
            scaleRel = rel,
        };
        if (DisplayConfigSetDeviceInfo(ref set) != 0)
        {
            throw new InvalidOperationException("设置显示缩放失败（DisplayConfigSetDeviceInfo）");
        }
        Console.WriteLine($"scale {currentDpi}% → {target}%");
        return 0;
    }

    private static int CurrentPrimaryScale()
    {
        var hMon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        GetDpiForMonitor(hMon, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _);
        return (int)(dpiX * 100 / 96);
    }

    /// <summary>
    /// 主显示器对应的 (adapterId, sourceId)。按 GDI 设备名匹配——DisplayConfig 的路径里
    /// 没有「哪个是主屏」的标志位，只能拿 MONITORINFOEX.szDevice 去对 viewGdiDeviceName。
    /// </summary>
    private static (LUID, uint) PrimarySource()
    {
        var hMon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMon, ref info);
        var primaryDevice = info.szDevice;

        var sizeRc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount);
        if (sizeRc != 0 || pathCount == 0)
        {
            throw new InvalidOperationException(
                $"GetDisplayConfigBufferSizes rc={sizeRc} paths={pathCount} modes={modeCount}" +
                (pathCount == 0
                    ? "——没有活动显示路径，无法定位主显示器。远程会话 / 虚拟显示适配器驱动的桌面"
                      + "通常不进显示配置库（CCD），这种情况下系统「设置」里的缩放同样改不动。"
                    : ""));
        }
        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
        {
            throw new InvalidOperationException(
                $"QueryDisplayConfig 失败 paths={pathCount}(struct {Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>()}B/应72) " +
                $"modes={modeCount}(struct {Marshal.SizeOf<DISPLAYCONFIG_MODE_INFO>()}B/应64)");
        }

        for (var i = 0; i < pathCount; i++)
        {
            var name = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = paths[i].sourceInfo.adapterId,
                    id = paths[i].sourceInfo.id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref name) != 0) continue;
            if (string.Equals(name.viewGdiDeviceName, primaryDevice, StringComparison.OrdinalIgnoreCase))
            {
                return (paths[i].sourceInfo.adapterId, paths[i].sourceInfo.id);
            }
        }
        throw new InvalidOperationException($"在活动显示路径里找不到主显示器 {primaryDevice}");
    }

    private static AutomationElement? PanelElement() =>
        AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, "Agent Timeline"),
                new PropertyCondition(AutomationElement.ClassNameProperty, MainWindowClass)));

    private static AutomationElement? Control(string automationId) =>
        PanelElement()?.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    private static int Rect(string automationId)
    {
        var e = Control(automationId);
        if (e is null) { Console.Error.WriteLine($"找不到控件 {automationId}"); return 1; }
        var r = e.Current.BoundingRectangle;
        Console.WriteLine($"{(int)r.X} {(int)r.Y} {(int)r.Width} {(int)r.Height} " +
                          $"{(int)(r.X + r.Width / 2)} {(int)(r.Y + r.Height / 2)}");
        return 0;
    }

    /// <summary>
    /// 主窗「窗口矩形 − 客户区」的差值：<c>dx dy dw dh</c>。
    ///
    /// AppSettings.WindowWidth/Height 存的是**窗口矩形**的物理像素，而可见面板是
    /// 客户区（本机实测四边各 7px 的不可见 resize 边框）。要让可见面板正好是
    /// 640×580dip，就得按这个差值反推窗口尺寸——差值随 DPI 变，别写死。
    /// </summary>
    private static int Border()
    {
        var main = AppWindows().FirstOrDefault(w => w.Class == MainWindowClass);
        if (main.Hwnd == IntPtr.Zero) { Console.Error.WriteLine("面板窗口不在"); return 1; }
        var c = ClientOnScreen(main.Hwnd, main.Rect);
        Console.WriteLine($"{c.OffX} {c.OffY} " +
                          $"{main.Rect.Right - main.Rect.Left - c.W} {main.Rect.Bottom - main.Rect.Top - c.H}");
        return 0;
    }

    private static int Invoke(string automationId)
    {
        var e = Control(automationId);
        if (e is null) { Console.Error.WriteLine($"找不到控件 {automationId}（UIA 树可能已退化，重启应用）"); return 1; }
        ((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        return 0;
    }

    // ═══════════════════════════════════════════════ 一次成型的拍摄

    private static int Shoot(string prefix, string[] opts)
    {
        string? invoke = null;
        (int X, int Y)? park = null;
        var settle = 2500;
        for (var i = 0; i < opts.Length; i++)
        {
            switch (opts[i])
            {
                case "--invoke": invoke = opts[++i]; break;
                case "--park": park = Pair(opts[++i]); break;
                case "--settle": settle = Int(opts[++i]); break;
            }
        }

        var main = AppWindows().FirstOrDefault(w => w.Class == MainWindowClass);
        if (main.Hwnd == IntPtr.Zero) { Console.Error.WriteLine("面板窗口不在"); return 1; }
        var mw = main.Rect.Right - main.Rect.Left;
        var mh = main.Rect.Bottom - main.Rect.Top;

        // 指针若停在面板上，hover 高亮与 tooltip 都会进成品。自己挪开——
        // 停放点在没给 --park 时由 ParkSpot 现算，调用方不必记得传。
        if (GetCursorPos(out var cursor) && InPanel(cursor))
        {
            var q = park ?? ParkSpotPair(main.Rect);
            MovePointer(q.X, q.Y);
            Thread.Sleep(800);
            if (GetCursorPos(out cursor) && InPanel(cursor))
            {
                Console.Error.WriteLine(
                    $"❌ 指针停在面板上（{cursor.X},{cursor.Y}），会摄进 hover 高亮与 tooltip；" +
                    $"挪到 {q.X},{q.Y} 没成功（SendInput 与 SetCursorPos 都被拦）。" +
                    "请手动把鼠标移开面板再重跑——不能默默拍一张带 hover 的图。");
                return 1;
            }
            Console.WriteLine($"   指针已移出面板 → {cursor.X},{cursor.Y}");
        }

        bool InPanel(POINT p) =>
            p.X >= main.Rect.Left && p.X < main.Rect.Right &&
            p.Y >= main.Rect.Top && p.Y < main.Rect.Bottom;

        Bitmap? baseline = null;
        if (invoke is not null)
        {
            var button = Control(invoke);
            if (button is null)
            {
                Console.Error.WriteLine($"❌ 找不到控件 {invoke}——UIA 树已退化，重启应用后再拍。");
                return 1;
            }
            baseline = CaptureClient(main.Hwnd, main.Rect);
            ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Thread.Sleep(settle);
        }

        if (baseline is not null)
        {
            using var after = CaptureClient(main.Hwnd, main.Rect);
            var changed = after is null ? 0 : DiffRatio(baseline, after);
            baseline.Dispose();
            if (changed < 0.02)
            {
                Console.Error.WriteLine(
                    $"❌ 调用 {invoke} 后面板几乎没变化（差异 {changed:P2}）——弹层没开。" +
                    "停下来，不要产出一张没有弹层的图。");
                return 1;
            }
            Console.Error.WriteLine($"   弹层已开（面板差异 {changed:P1}）");
        }

        var idx = 0;
        foreach (var (hwnd, rect, cls, _) in AppWindows())
        {
            var c = ClientOnScreen(hwnd, rect);
            var file = $"{prefix}-{idx}.png";
            using var bmp = CaptureClient(hwnd, rect);
            if (bmp is null) { Console.Error.WriteLine($"抓 {hwnd} 失败"); return 1; }
            bmp.Save(file, ImageFormat.Png);
            // 回显的是**客户区**的屏幕坐标与尺寸——合成要按这个摆位
            Console.WriteLine($"{idx} {hwnd.ToInt64()} {c.X} {c.Y} {bmp.Width} {bmp.Height} {cls} {file}");
            idx++;
        }
        return idx > 0 ? 0 : 1;
    }

    /// <summary>两张同尺寸抓取里颜色不同的像素占比（步长 2 抽样，够判"变没变"）。</summary>
    private static unsafe double DiffRatio(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 1.0;
        var ra = a.LockBits(new Rectangle(0, 0, a.Width, a.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var rb = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var pa = (byte*)ra.Scan0;
        var pb = (byte*)rb.Scan0;
        long diff = 0, total = 0;
        for (var y = 0; y < a.Height; y += 2)
        {
            for (var x = 0; x < a.Width; x += 2)
            {
                var oa = y * ra.Stride + x * 4;
                var ob = y * rb.Stride + x * 4;
                total++;
                if (Math.Abs(pa[oa] - pb[ob]) > 8 ||
                    Math.Abs(pa[oa + 1] - pb[ob + 1]) > 8 ||
                    Math.Abs(pa[oa + 2] - pb[ob + 2]) > 8) diff++;
            }
        }
        a.UnlockBits(ra);
        b.UnlockBits(rb);
        return total == 0 ? 0 : (double)diff / total;
    }

    // ═══════════════════════════════════════════════ 窗口抓取

    private static int Shot(IntPtr hwnd, string outPath)
    {
        if (!GetWindowRect(hwnd, out var rect)) { Console.Error.WriteLine("窗口不存在"); return 1; }
        var c = ClientOnScreen(hwnd, rect);
        using var bmp = CaptureClient(hwnd, rect);
        if (bmp is null) { Console.Error.WriteLine("PrintWindow 失败"); return 1; }
        bmp.Save(outPath, ImageFormat.Png);
        Console.WriteLine($"{bmp.Width}x{bmp.Height} @ {c.X},{c.Y} → {System.IO.Path.GetFileName(outPath)}");
        return 0;
    }

    /// <summary>
    /// 窗口矩形里**客户区**的屏幕位置与尺寸，以及客户区在窗口矩形内的偏移。
    ///
    /// 为什么必须裁到客户区：GetWindowRect 比可见面板大一圈（本机实测四边各 7px，
    /// 那是不可见的 resize 抓取边框），PrintWindow 会把这一圈画成垃圾——顶边一条
    /// 浅色带、其余三边纯黑。叠到深色背板上就是很扎眼的白边（实测踩过）。
    /// </summary>
    private static (int X, int Y, int W, int H, int OffX, int OffY) ClientOnScreen(IntPtr hwnd, RECT window)
    {
        GetClientRect(hwnd, out var cr);
        var origin = new POINT();
        ClientToScreen(hwnd, ref origin);
        return (origin.X, origin.Y, cr.Right - cr.Left, cr.Bottom - cr.Top,
                origin.X - window.Left, origin.Y - window.Top);
    }

    /// <summary>
    /// PrintWindow 到 32bpp 顶朝下 DIB，再裁到客户区。PW_RENDERFULLCONTENT 走 DWM
    /// 重绘，DirectComposition 渲染的 WinUI 3 内容才不会是黑帧。
    ///
    /// alpha 兜底：个别窗口 PrintWindow 不写 alpha 通道，整张 alpha=0，直接存 PNG
    /// 就是一张全透明图（存盘时看不出来，合成后才发现"图没了"）。全 0 时判为不透明。
    /// </summary>
    private static Bitmap? CaptureClient(IntPtr hwnd, RECT window)
    {
        var c = ClientOnScreen(hwnd, window);
        using var full = CaptureWindow(hwnd, window.Right - window.Left, window.Bottom - window.Top);
        if (full is null) return null;
        if (c.W <= 0 || c.H <= 0 || c.OffX < 0 || c.OffY < 0 ||
            c.OffX + c.W > full.Width || c.OffY + c.H > full.Height)
        {
            return new Bitmap(full);     // 客户区算不出来就退回整窗，别丢图
        }
        return full.Clone(new Rectangle(c.OffX, c.OffY, c.W, c.H), PixelFormat.Format32bppArgb);
    }

    private static unsafe Bitmap? CaptureWindow(IntPtr hwnd, int w, int h)
    {
        var hdcScreen = GetDC(IntPtr.Zero);
        var hdcMem = CreateCompatibleDC(hdcScreen);
        var bi = new BITMAPINFO
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,               // 负高 = 顶朝下，与 GDI+ 扫描线同向
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,           // BI_RGB
        };
        var hBmp = CreateDIBSection(hdcMem, ref bi, 0, out var bits, IntPtr.Zero, 0);
        if (hBmp == IntPtr.Zero) { Cleanup(); return null; }
        var old = SelectObject(hdcMem, hBmp);

        var ok = PrintWindow(hwnd, hdcMem, PW_RENDERFULLCONTENT);
        SelectObject(hdcMem, old);
        if (!ok) { DeleteObject(hBmp); Cleanup(); return null; }

        var px = (byte*)bits;
        var count = w * h;
        byte maxAlpha = 0;
        for (var i = 0; i < count; i++) { var a = px[i * 4 + 3]; if (a > maxAlpha) maxAlpha = a; }
        if (maxAlpha == 0) { for (var i = 0; i < count; i++) px[i * 4 + 3] = 255; }

        // DWM 给的是预乘 BGRA，按 PArgb 包装，GDI+ 绘制/存盘都按预乘处理。
        using var wrapper = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
        var copy = new Bitmap(wrapper);          // 脱离 DIB 内存，随后可安全释放
        DeleteObject(hBmp);
        Cleanup();
        return copy;

        void Cleanup()
        {
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    /// <summary>
    /// 指针的停放点：面板所在显示器上离面板中心最远的那个角，内缩 12px。
    /// 取「最远的角」而不是写死左上——面板本身就可能贴在左上。
    /// </summary>
    private static (int X, int Y) ParkSpotPair(RECT panel)
    {
        var hMon = MonitorFromPoint(
            new POINT { X = (panel.Left + panel.Right) / 2, Y = (panel.Top + panel.Bottom) / 2 },
            2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMon, ref info);
        var m = info.rcWork;

        var cx = (panel.Left + panel.Right) / 2.0;
        var cy = (panel.Top + panel.Bottom) / 2.0;
        (int X, int Y) best = default;
        var bestDist = -1.0;
        foreach (var (x, y) in new[]
                 {
                     (m.Left + 12, m.Top + 12), (m.Right - 12, m.Top + 12),
                     (m.Left + 12, m.Bottom - 12), (m.Right - 12, m.Bottom - 12),
                 })
        {
            var d = (x - cx) * (x - cx) + (y - cy) * (y - cy);
            if (d > bestDist) { bestDist = d; best = (x, y); }
        }
        return best;
    }

    /// <summary>
    /// 把指针挪到指定屏幕坐标，**自己核对是否真的动了**。
    ///
    /// 两条路依次试：`SendInput`（会走完整输入栈，某些远程/防护软件会拦）→ `SetCursorPos`
    /// （直接置位，不产生输入事件）。曾经只有 SendInput 一条路，在某些环境下静默失败——
    /// 前后 GetCursorPos 完全不变，而调用返回成功。所以这里以**读回坐标**为准，
    /// 不看返回值；两条都不成才报失败。
    /// </summary>
    private static int MovePointer(int x, int y)
    {
        var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        // SendInput 绝对坐标是整个虚拟桌面的 0..65535 归一化值，不是像素。
        var input = new INPUT
        {
            type = 0,
            mi = new MOUSEINPUT
            {
                dx = (int)((x - vx) * 65535.0 / (vw - 1)),
                dy = (int)((y - vy) * 65535.0 / (vh - 1)),
                dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(120);
        if (Near(x, y, out var got)) { Console.WriteLine($"pointer → {got.X},{got.Y} (SendInput)"); return 0; }

        SetCursorPos(x, y);
        Thread.Sleep(120);
        if (Near(x, y, out got)) { Console.WriteLine($"pointer → {got.X},{got.Y} (SetCursorPos)"); return 0; }

        Console.Error.WriteLine($"失败: 指针挪不动，仍在 {got.X},{got.Y}（目标 {x},{y}）——" +
                                "SendInput 与 SetCursorPos 都被拦（远程/防护软件常见）");
        return 1;

        // 容差 2px：某些驱动会把坐标吸附到整数缩放格点上。
        static bool Near(int tx, int ty, out POINT p)
        {
            GetCursorPos(out p);
            return Math.Abs(p.X - tx) <= 2 && Math.Abs(p.Y - ty) <= 2;
        }
    }

    // ═══════════════════════════════════════════════ 合成到统一画布

    // 视觉常量与 mac compose.swift、README 首图、社媒图同源；改这里要一起改。
    private static readonly Color Backplate = Color.FromArgb(255, 16, 16, 20);   // #101014
    private const int ShadowDy = 26;          // mac: offset (0, −26)，Cocoa y 轴朝上 = 屏幕朝下
    private const int ShadowBlur = 60;
    private const double ShadowAlpha = 0.55;

    /// <summary>
    /// mac 成品画布宽度（1718px @2x）。投影的位移与模糊是**像素**常量，本端若不是
    /// 2x 出图，照抄像素值会让阴影在 dip 上大一倍。按画布宽与它的比值折算，
    /// 观感才与 mac 一致——也免得多一个必须记得传对的参数。
    /// </summary>
    private const double MacCanvasWidth = 1718.0;

    /// <summary>
    /// 面板圆角（design/design-tokens.json 的 radius.panel = 14dip）。
    /// PrintWindow 抓的是客户区位图，**不带 DWM 的圆角裁剪**——直接叠到背板上是
    /// 四个直角，与 mac 的 screencapture 产出（自带圆角与 alpha）观感对不上。
    /// 在合成阶段按同一 token 补回来。
    /// </summary>
    private const double PanelRadiusDip = 14.0;
    private const double MacCanvasWidthDip = 859.0;

    /// <summary>
    /// 把若干张窗口抓取按各自**屏幕坐标**摆到统一画布上：并集包围盒居中，
    /// 补背板 + 光晕 + 投影。
    ///
    /// 为什么合成阶段要补背板：窗口抓取只有窗口自己的像素，窗口之间/溢出部分
    /// 背后是空的，直接摆上去会留接缝和发黑区域。别改用屏幕区域截图去"接住"
    /// ——那会把盖在上面的第三方浮层一起摄进来。
    /// </summary>
    private static int Compose(string outPath, int canvasW, int canvasH, string[] specs)
    {
        // 圆角半径随画布缩放：canvasW / 859dip 即本次的 dip→px 倍率
        var radius = (int)Math.Round(PanelRadiusDip * canvasW / MacCanvasWidthDip);
        var layers = new List<(Bitmap Img, int X, int Y)>();
        foreach (var spec in specs)
        {
            var at = spec.LastIndexOf('@');
            if (at < 0) throw new ArgumentException($"输入要写成 <png>@<x>,<y>：{spec}");
            var (x, y) = Pair(spec[(at + 1)..]);
            layers.Add((RoundCorners(new Bitmap(spec[..at]), radius), x, y));
        }

        var minX = layers.Min(l => l.X);
        var minY = layers.Min(l => l.Y);
        var unionW = layers.Max(l => l.X + l.Img.Width) - minX;
        var unionH = layers.Max(l => l.Y + l.Img.Height) - minY;
        if (unionW > canvasW || unionH > canvasH)
        {
            Console.Error.WriteLine($"画布 {canvasW}x{canvasH} 装不下并集 {unionW}x{unionH}");
            return 1;
        }
        var offX = (canvasW - unionW) / 2 - minX;
        var offY = (canvasH - unionH) / 2 - minY;

        using var canvas = new Bitmap(canvasW, canvasH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Backplate);
            Glow(g, canvasW * 0.12, canvasH * 0.94, canvasW * 0.55, Color.FromArgb(35, 40, 56));
            Glow(g, canvasW * 0.95, canvasH * 0.10, canvasW * 0.50, Color.FromArgb(29, 36, 54));
        }
        using (var shadow = ShadowLayer(layers, canvasW, canvasH, offX, offY))
        using (var g = Graphics.FromImage(canvas))
        {
            g.CompositingMode = CompositingMode.SourceOver;
            g.DrawImage(shadow, 0, 0);
        }
        using (var g = Graphics.FromImage(canvas))
        {
            g.CompositingMode = CompositingMode.SourceOver;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;   // 1:1 摆放，不重采样
            g.PixelOffsetMode = PixelOffsetMode.Half;
            foreach (var (img, x, y) in layers) g.DrawImage(img, x + offX, y + offY, img.Width, img.Height);
        }

        canvas.Save(outPath, ImageFormat.Png);
        foreach (var (img, _, _) in layers) img.Dispose();
        Console.WriteLine($"{canvasW}x{canvasH}（并集 {unionW}x{unionH}）→ {System.IO.Path.GetFileName(outPath)}");
        return 0;
    }

    /// <summary>把位图裁成圆角（角外 alpha=0，边缘抗锯齿）。投影掩膜随后读的就是这份 alpha。</summary>
    private static Bitmap RoundCorners(Bitmap src, int radius)
    {
        if (radius <= 0) return src;
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRect(src.Width, src.Height, radius);
            using var brush = new TextureBrush(src);   // 从 (0,0) 起 1:1 铺，不缩放
            g.FillPath(brush, path);
        }
        src.Dispose();
        return dst;
    }

    private static GraphicsPath RoundedRect(int w, int h, int r)
    {
        var d = r * 2;
        var path = new GraphicsPath();
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(w - d, 0, d, d, 270, 90);
        path.AddArc(w - d, h - d, d, d, 0, 90);
        path.AddArc(0, h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void Glow(Graphics g, double cx, double cy, double r, Color color)
    {
        using var path = new GraphicsPath();
        path.AddEllipse((float)(cx - r), (float)(cy - r), (float)(r * 2), (float)(r * 2));
        using var brush = new PathGradientBrush(path)
        {
            CenterPoint = new PointF((float)cx, (float)cy),
            CenterColor = Color.FromArgb(128, color),            // mac: alpha 0.5
            SurroundColors = new[] { Color.FromArgb(0, color) },
        };
        g.FillPath(brush, path);
    }

    /// <summary>
    /// 投影：GDI+ 没有内建高斯模糊，用三次盒糊近似。所有图层的轮廓先并到同一张
    /// alpha 掩膜再糊，图层之间才不会互相盖出硬边。
    /// </summary>
    private static unsafe Bitmap ShadowLayer(
        List<(Bitmap Img, int X, int Y)> layers, int w, int h, int offX, int offY)
    {
        var k = w / MacCanvasWidth;
        var dy = (int)Math.Round(ShadowDy * k);
        var blur = (int)Math.Round(ShadowBlur * k);
        var mask = new byte[w * h];
        foreach (var (img, x, y) in layers)
        {
            var data = img.LockBits(new Rectangle(0, 0, img.Width, img.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var src = (byte*)data.Scan0;
            for (var iy = 0; iy < img.Height; iy++)
            {
                var cy = y + offY + iy + dy;
                if (cy < 0 || cy >= h) continue;
                for (var ix = 0; ix < img.Width; ix++)
                {
                    var cx = x + offX + ix;
                    if (cx < 0 || cx >= w) continue;
                    var a = src[iy * data.Stride + ix * 4 + 3];
                    var i = cy * w + cx;
                    if (a > mask[i]) mask[i] = a;
                }
            }
            img.UnlockBits(data);
        }

        var r = blur / 3;                     // 三次半径 r 的盒糊 ≈ 扩散 3r ≈ blur
        for (var pass = 0; pass < 3; pass++) BoxBlur(mask, w, h, r);

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var dst = (byte*)bd.Scan0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = y * bd.Stride + x * 4;
                dst[o] = 0; dst[o + 1] = 0; dst[o + 2] = 0;
                dst[o + 3] = (byte)(mask[y * w + x] * ShadowAlpha);
            }
        }
        bmp.UnlockBits(bd);
        return bmp;
    }

    /// <summary>可分离盒糊：先横后纵，各 O(n)（滑动窗口和）。</summary>
    private static void BoxBlur(byte[] a, int w, int h, int r)
    {
        if (r <= 0) return;
        var tmp = new byte[a.Length];
        var win = r * 2 + 1;
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            var sum = 0;
            for (var x = -r; x <= r; x++) sum += a[row + Math.Clamp(x, 0, w - 1)];
            for (var x = 0; x < w; x++)
            {
                tmp[row + x] = (byte)(sum / win);
                sum += a[row + Math.Clamp(x + r + 1, 0, w - 1)] - a[row + Math.Clamp(x - r, 0, w - 1)];
            }
        }
        for (var x = 0; x < w; x++)
        {
            var sum = 0;
            for (var y = -r; y <= r; y++) sum += tmp[Math.Clamp(y, 0, h - 1) * w + x];
            for (var y = 0; y < h; y++)
            {
                a[y * w + x] = (byte)(sum / win);
                sum += tmp[Math.Clamp(y + r + 1, 0, h - 1) * w + x] - tmp[Math.Clamp(y - r, 0, h - 1) * w + x];
            }
        }
    }

    // ═══════════════════════════════════════════════ P/Invoke

    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int reserved1, reserved2, reserved3, reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
        // MOUSEINPUT 是 INPUT 联合里最大的成员，用它铺满即可（只发鼠标事件）。
        private readonly IntPtr _pad1;
        private readonly IntPtr _pad2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(
        IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    // ─────────────────────────── 主显示器缩放（读 / 写）
    //
    // 走的是 DisplayConfig 那套，和系统「设置 → 显示 → 缩放」同一条路径：**立即生效**，
    // 不需要注销重登。GET/SET_DPI_SCALE 这两个 info type 微软没写进文档（负数保留段），
    // 但从 Win10 1703 起一直可用，Settings 应用自己就在用。
    //
    // 关键点：`scaleRel` 是**相对系统推荐缩放的档位偏移**，不是绝对百分比。所以要先 GET
    // 拿到 (min, cur, max) 三个相对值，用 cur 反推出推荐档在 DpiSteps 里的下标，
    // 再算目标档的相对偏移。直接写绝对百分比是错的。
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(
        uint flags, ref uint pathCount, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint modeCount, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topologyId);

    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(
        ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);

    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(
        ref DISPLAYCONFIG_SOURCE_DPI_SCALE_GET request);

    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(
        ref DISPLAYCONFIG_SOURCE_DPI_SCALE_SET request);

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE = -3;
    private const int DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE = -4;

    /// <summary>Windows 允许的缩放档位，顺序即档位序号。</summary>
    private static readonly int[] DpiSteps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public int size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SOURCE_DPI_SCALE_GET
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public int minScaleRel;
        public int curScaleRel;
        public int maxScaleRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SOURCE_DPI_SCALE_SET
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public int scaleRel;
    }

    // 路径/模式结构只用来定位主显示器的 (adapterId, sourceId)，字段按大小占位即可——
    // 只有 sourceInfo 前两个字段被真正读取，其余保持布局正确就行。
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        // DISPLAYCONFIG_RATIONAL 是两个 uint32、按 4 字节对齐。写成 ulong 会把对齐提到 8，
        // 前面插进 4 字节 padding，整个结构体错位——路径数组一错，主显示器就找不着了。
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        // 其后是 64 字节的 union（target/source/desktopImage），本工具不读，靠 Size 占位。
    }
}
