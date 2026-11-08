using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace MikuGuard.Views.Components;

/// <summary>
/// 岛内涟漪：以 MD3 的 fast-out-slow-in 缓动从一点扩散开的实心圆，裁剪在岛的形状里。
/// 自己用 16ms 时钟推进，不依赖宿主的动画系统。
/// </summary>
internal sealed class IslandRipple : Control
{
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly double _durationMs;
    private readonly double _strength;
    private readonly double _maxRadius;
    private readonly Point _center;
    private readonly Color _color;
    private readonly Rect _clipRect;
    private readonly double _cornerRadius;
    private DispatcherTimer? _timer;

    private const double PeakRatio = 0.12;    // 前 12% 时间里把不透明度拉满
    private const double FadeStart = 0.62;    // 62% 之后开始衰减

    public event Action<IslandRipple>? Completed;

    public IslandRipple(Point center, double maxRadius, Color color, double durationMs, double strength,
        Rect clipRect, double cornerRadius)
    {
        _center = center;
        _maxRadius = Math.Max(maxRadius, 1);
        _color = color;
        _durationMs = Math.Clamp(durationMs, 100, 5000);
        _strength = Math.Clamp(strength, 0.02, 0.9);
        _clipRect = clipRect;
        _cornerRadius = Math.Max(0, cornerRadius);

        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
        _timer = null;
    }

    private void Tick()
    {
        if (Elapsed() >= 1)
        {
            _timer?.Stop();
            _timer = null;
            Completed?.Invoke(this);
            return;
        }

        InvalidateVisual();
    }

    private double Elapsed() =>
        Math.Clamp((DateTime.UtcNow - _startedAt).TotalMilliseconds / _durationMs, 0, 1);

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var t = Elapsed();
        if (t >= 1) return;

        var radius = _maxRadius * Md3Easing.FastOutSlowIn(t);
        if (radius <= 0.5) return;

        var alpha = _strength * FadeIn(t) * FadeOut(t);
        if (alpha <= 0.001) return;

        var brush = new SolidColorBrush(_color, alpha);

        // 裁剪到岛的渲染矩形（叠加层铺满窗口，本地坐标 == WindowRoot 坐标）
        using var clip = context.PushClip(new RoundedRect(_clipRect, _cornerRadius));

        context.DrawEllipse(brush, null, _center, radius, radius);
    }

    private static double FadeIn(double t) => Math.Clamp(t / PeakRatio, 0, 1);

    private static double FadeOut(double t)
    {
        if (t <= FadeStart) return 1;
        var k = (t - FadeStart) / (1 - FadeStart);
        return 1 - k * k;   // 尾部平方衰减，收得干净
    }
}

/// <summary>MD3 的缓动曲线（cubic-bezier）。</summary>
internal static class Md3Easing
{
    /// <summary>FastOutSlowIn = cubic-bezier(0.4, 0.0, 0.2, 1.0)，MD3 的 ripple 用的就是它。</summary>
    public static double FastOutSlowIn(double t) => CubicBezier(0.4, 0.0, 0.2, 1.0, t);

    public static double CubicBezier(double x1, double y1, double x2, double y2, double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;

        // 先由 x 反解参数 t，再取 y(t)
        var lo = 0.0;
        var hi = 1.0;
        var tParam = x;
        for (var i = 0; i < 24; i++)
        {
            tParam = (lo + hi) / 2;
            var mid = BezierAxis(tParam, x1, x2);
            if (Math.Abs(mid - x) < 1e-5) break;
            if (mid < x) lo = tParam;
            else hi = tParam;
        }

        return BezierAxis(tParam, y1, y2);
    }

    private static double BezierAxis(double t, double p1, double p2)
    {
        var u = 1 - t;
        return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
    }
}
