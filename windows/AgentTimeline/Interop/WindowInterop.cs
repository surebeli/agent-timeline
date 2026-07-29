using System.Runtime.InteropServices;

namespace AgentTimeline.Interop;

/// <summary>
/// Win32 interop for the floating-panel behaviors WinUI 3 does not expose directly.
///
/// Whole-window opacity: a WinUI 3 Window has no Opacity property, so we use the classic
/// layered-window mechanism —
///   1. add WS_EX_LAYERED to the top-level HWND's extended style (GWL_EXSTYLE);
///   2. call SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA).
/// This fades EVERYTHING including the acrylic backdrop, matching the mac
/// `animator().alphaValue` behavior. On some Windows builds layered alpha can suppress the
/// acrylic material; OpacityAnimator therefore has a fallback mode that animates the XAML
/// root's Opacity instead (content-only fade, backdrop stays solid).
///
/// Window dragging: 曾用经典的 ReleaseCapture + WM_NCLBUTTONDOWN/HTCAPTION 技巧借系统
/// 原生移动循环，**在 WinUI 3 下不可靠**——指针输入走的是 XAML island 的 input site 而不是
/// 顶层 HWND，模态循环常常在按键已经松开之后才启动、于是在等一个早就发生过的 WM_LBUTTONUP。
/// 实机症状：按住不动拖不走，点一下松开窗口反而黏着鼠标跑（2026-07-29 有人值守发现）。
/// 现改为手动拖拽（捕获指针 + AppWindow.Move，见 MainWindow.HeaderBar_Pointer*），
/// 不进系统模态循环。这里只留取屏幕坐标的辅助方法。
/// </summary>
public static partial class WindowInterop
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    /// <summary>
    /// 窗口所在显示器的缩放系数（96dpi=1.0）。design tokens 的面板尺寸是逻辑像素，
    /// AppWindow API 吃物理像素——高 DPI 下不乘系数面板会整体偏小（150% 下 340→视觉 227）。
    /// </summary>
    public static double GetWindowScale(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>Sets whole-window opacity (0.0–1.0) via the layered-window alpha channel.</summary>
    public static void SetWindowOpacity(IntPtr hwnd, double opacity)
    {
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((style & WS_EX_LAYERED) == 0)
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_LAYERED);
        }
        var alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255.0), 0, 255);
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }

    /// <summary>
    /// 指针的**屏幕**坐标（物理像素，与 <c>AppWindow.Position</c>/<c>Move</c> 同一坐标系）。
    ///
    /// 拖窗口用它算位移、而不是用 <c>PointerRoutedEventArgs.GetCurrentPoint</c>：后者是
    /// 相对元素的逻辑坐标，跨 DPI 不同的显示器拖动时换算会漂。
    /// </summary>
    public static bool TryGetCursorPos(out Windows.Graphics.PointInt32 point)
    {
        if (GetCursorPos(out var p))
        {
            point = new Windows.Graphics.PointInt32(p.X, p.Y);
            return true;
        }
        point = default;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
