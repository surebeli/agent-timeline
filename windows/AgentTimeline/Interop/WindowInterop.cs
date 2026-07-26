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
/// Window dragging: the window is borderless (no title bar), so dragging is implemented
/// with the classic ReleaseCapture + WM_NCLBUTTONDOWN/HTCAPTION trick — Windows then runs
/// its native move loop as if the user grabbed a real caption bar.
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

    /// <summary>Starts the native window move loop (call from a PointerPressed handler).</summary>
    public static void BeginWindowDrag(IntPtr hwnd)
    {
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }
}
