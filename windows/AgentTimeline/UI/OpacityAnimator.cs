using AgentTimeline.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AgentTimeline.UI;

/// <summary>
/// Animates the floating panel between the two design-token opacity levels
/// (opacity.hover = 0.95 on pointer enter, opacity.idle = 0.25 on pointer exit /
/// deactivate)。**快淡入、慢淡出**：变亮走 opacity.transitionMs(180ms) + ease-out，
/// 变暗走 opacity.transitionOutMs(500ms) + ease-in——指针一进来立刻可读，移开时从容
/// 化开，避免'看到一半就唰地消失'。
///
/// Primary mode drives whole-window alpha through WindowInterop.SetWindowOpacity
/// (layered window — see the interop notes there). If acrylic misbehaves with layered
/// alpha on the target Windows build, flip <see cref="UseLayeredWindowAlpha"/> to false
/// to animate the XAML root element's Opacity instead (content-only fade).
///
/// A DispatcherQueueTimer at ~60 fps is used rather than a Composition animation because
/// the animated value lives outside the visual tree in the primary (Win32) mode.
/// </summary>
public sealed class OpacityAnimator
{
    /// <summary>Pragmatic switch between Win32 layered alpha and XAML root opacity.</summary>
    public const bool UseLayeredWindowAlpha = true;

    private readonly IntPtr _hwnd;
    private readonly UIElement _root;
    private readonly DispatcherQueueTimer _timer;
    private readonly double _inMs;
    private readonly double _outMs;

    private double _current = 1.0;
    private double _from = 1.0;
    private double _target = 1.0;
    private double _durationMs;
    private bool _fadingOut;
    private DateTime _startedAt;

    public OpacityAnimator(
        IntPtr hwnd, UIElement root, DispatcherQueue dispatcher, double inMs, double outMs)
    {
        _hwnd = hwnd;
        _root = root;
        _inMs = Math.Max(1, inMs);
        _outMs = Math.Max(1, outMs);
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16); // ~60 fps
        _timer.Tick += (_, _) => Step();
    }

    public void SetImmediate(double opacity)
    {
        _timer.Stop();
        _current = _target = Math.Clamp(opacity, 0.0, 1.0);
        Apply(_current);
    }

    public void AnimateTo(double opacity)
    {
        opacity = Math.Clamp(opacity, 0.0, 1.0);
        if (Math.Abs(opacity - _target) < 0.001 && _timer.IsRunning) return;
        if (Math.Abs(opacity - _current) < 0.001) { _target = opacity; return; }

        _from = _current;
        _target = opacity;
        // 方向决定时长与曲线：变亮走 transitionMs + ease-out（立刻可读），
        // 变暗走 transitionOutMs + ease-in（先稳住再化开）。
        _fadingOut = _target < _from;
        _durationMs = _fadingOut ? _outMs : _inMs;
        _startedAt = DateTime.UtcNow;
        _timer.Start();
    }

    private void Step()
    {
        var t = (DateTime.UtcNow - _startedAt).TotalMilliseconds / _durationMs;
        if (t >= 1.0)
        {
            _current = _target;
            _timer.Stop();
        }
        else
        {
            // 只拉长淡出时长而不换曲线是不够的：ease-out 会把绝大部分变化挤在前段，
            // 500ms 下观感变成「唰一下再慢慢爬」，比原来更别扭。淡出改 ease-in。
            var eased = _fadingOut
                ? Math.Pow(t, 3.0)                  // ease-in cubic：先稳住，后段化开
                : 1.0 - Math.Pow(1.0 - t, 3.0);     // ease-out cubic：立刻可读
            _current = _from + (_target - _from) * eased;
        }
        Apply(_current);
    }

    private void Apply(double opacity)
    {
#pragma warning disable CS0162 // intentional constant-mode switch
        if (UseLayeredWindowAlpha)
        {
            WindowInterop.SetWindowOpacity(_hwnd, opacity);
        }
        else
        {
            _root.Opacity = opacity;
        }
#pragma warning restore CS0162
    }
}
