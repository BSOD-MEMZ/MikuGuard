using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MikuGuard.Services.Hanabi;

/// <summary>
/// 全屏花火叠加层：一块透明、置顶、鼠标穿透的整屏窗口，里面画一次开花。
/// 素材与动画参数来自 thock-maimai（见 Assets/Hanabi/ASSET-LICENSE.txt）。
/// </summary>
public sealed class HanabiOverlayWindow : Window
{
    private readonly HanabiSurface _surface;

    /// <summary>画完自动收摊。</summary>
    public event Action? Finished;

    public HanabiOverlayWindow(HanabiSimulation simulation, double originX, double originY)
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "MikuGuard Hanabi";
        Focusable = false;

        _surface = new HanabiSurface(simulation, originX, originY);
        Content = _surface;
        _surface.Finished += () => Finished?.Invoke();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        CoverScreen();
        MakeClickThrough();
        _surface.Start();
    }

    /// <summary>铺满鼠标所在的那块屏幕。</summary>
    private void CoverScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) return;

        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / scale;
        Height = screen.Bounds.Height / scale;
    }

    // ---------------- Win32：鼠标穿透 + 不抢焦点 ----------------

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private void MakeClickThrough()
    {
        try
        {
            var handle = TryGetPlatformHandle()?.Handle;
            if (handle == null || handle == IntPtr.Zero) return;

            var style = GetWindowLongPtr(handle.Value, GwlExStyle).ToInt64();
            style |= WsExTransparent | WsExNoActivate | WsExToolWindow;
            SetWindowLongPtr(handle.Value, GwlExStyle, new IntPtr(style));
        }
        catch (Exception ex)
        {
            GuardLog.Warn($"[hanabi] 设置鼠标穿透失败：{ex.Message}");
        }
    }
}

/// <summary>画烟花的那块画布。</summary>
internal sealed class HanabiSurface : Control
{
    private static readonly Dictionary<string, Bitmap> Masks = new(StringComparer.OrdinalIgnoreCase);

    private readonly HanabiSimulation _sim;
    private readonly double _originX;
    private readonly double _originY;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly DispatcherTimer _timer;

    public event Action? Finished;

    public HanabiSurface(HanabiSimulation simulation, double originX, double originY)
    {
        _sim = simulation;
        _originX = originX;
        _originY = originY;
        IsHitTestVisible = false;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
    }

    private double Now => (DateTime.UtcNow - _startedAt).TotalSeconds;

    public void Start()
    {
        _sim.Burst(Now, _originX, _originY);
        _timer.Start();
    }

    private void Tick()
    {
        // 花开完 + 一点余量就结束，别挂着空窗口吃帧
        if (Now > HanabiSimulation.BurstLifetime + 0.15 && _sim.Bursts.Count == 0)
        {
            _timer.Stop();
            Finished?.Invoke();
            return;
        }

        _sim.Expire(Now);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Bounds.Width < 2 || Bounds.Height < 2 || !_sim.Ready) return;

        var now = Now;
        var scale = Math.Max(Bounds.Width, Bounds.Height) / 1000.0;

        for (var layer = 0; layer < 3; layer++)
        {
            foreach (var burst in _sim.Bursts)
            {
                foreach (var particle in burst.Particles)
                {
                    var emitter = _sim.Emitters[particle.Emitter];
                    if (emitter.Layer != layer) continue;

                    if (!HanabiSimulation.Sample(emitter, particle, now, burst.Start, scale, out var sample)) continue;
                    if (sample.Diameter <= 0.5 || sample.A <= 0.004) continue;

                    var x = Bounds.Width * burst.OriginX + Math.Cos(particle.Angle) * sample.Distance * scale;
                    var y = Bounds.Height * burst.OriginY - Math.Sin(particle.Angle) * sample.Distance * scale;

                    var color = Color.FromRgb((byte)(sample.R * 255), (byte)(sample.G * 255), (byte)(sample.B * 255));

                    switch (emitter.Material)
                    {
                        case 49:
                            DrawRainbowRing(context, x, y, sample, color);
                            break;

                        case 50:
                            DrawSprite(context, MaskFor("ring_4862.png"), x, y, sample, Colors.White, sample.A);
                            break;

                        default:
                            var mask = MaskFor(emitter.Textures.Length > 0 ? MaskName(emitter.Textures[0]) : null);
                            if (mask != null) DrawSprite(context, mask, x, y, sample, color, sample.A);
                            break;
                    }
                }
            }
        }
    }

    /// <summary>贴图光源：遮罩当不透明度，纯色填充 = 原 shader 的 tex * tint。</summary>
    private static void DrawSprite(DrawingContext context, Bitmap? mask, double x, double y, in HanabiSample sample,
        Color tint, double alpha)
    {
        if (mask == null) return;

        var size = sample.Diameter;
        var rect = new Rect(x - size / 2, y - size / 2, size, size);
        var brush = new ImageBrush(mask) { Stretch = Stretch.Fill };

        var matrix = Matrix.CreateTranslation(-x, -y)
                     * Matrix.CreateRotation(sample.Rotation)
                     * Matrix.CreateTranslation(x, y);

        using (context.PushTransform(matrix))
        using (context.PushOpacityMask(brush, rect))
        {
            context.FillRectangle(new SolidColorBrush(tint, Math.Clamp(alpha, 0, 1)), rect);
        }
    }

    /// <summary>彩虹光环没有贴图，原实现是 shader 里按角度生成色相，这里用锥形渐变近似。</summary>
    private static void DrawRainbowRing(DrawingContext context, double x, double y, in HanabiSample sample, Color tint)
    {
        var radius = sample.Diameter / 2;
        if (radius < 1) return;

        var brush = new ConicGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(255, 255, 0, 0), 0.00),
                new GradientStop(Color.FromArgb(255, 255, 255, 0), 0.17),
                new GradientStop(Color.FromArgb(255, 0, 255, 0), 0.33),
                new GradientStop(Color.FromArgb(255, 0, 255, 255), 0.50),
                new GradientStop(Color.FromArgb(255, 0, 120, 255), 0.67),
                new GradientStop(Color.FromArgb(255, 200, 0, 255), 0.83),
                new GradientStop(Color.FromArgb(255, 255, 0, 0), 1.00)
            }
        };

        var pen = new Pen(brush, Math.Max(2, radius * 0.16));
        using (context.PushOpacity(Math.Clamp(sample.A, 0, 1)))
        {
            context.DrawEllipse(null, pen, new Point(x, y), radius, radius);
        }
    }

    private static string? MaskName(string texture) => texture switch
    {
        "maimai_1519.png" => "mask_1519.png",
        "maimai_2694.png" => "mask_2694.png",
        "maimai_3691.png" => "mask_2694.png",   // 备用；正常走 ring_4862
        "maimai_4862.png" => "ring_4862.png",
        _ => null
    };

    private static Bitmap? MaskFor(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        if (Masks.TryGetValue(fileName, out var cached)) return cached;

        try
        {
            var path = MikuGuardPaths.Resolve(Path.Combine("Assets", "Hanabi", fileName));
            if (!File.Exists(path))
            {
                GuardLog.Warn($"[hanabi] 缺少贴图：{path}");
                Masks[fileName] = null!;
                return null;
            }

            var bitmap = new Bitmap(path);
            Masks[fileName] = bitmap;
            return bitmap;
        }
        catch (Exception ex)
        {
            GuardLog.Error($"[hanabi] 贴图加载失败：{fileName}", ex);
            Masks[fileName] = null!;
            return null;
        }
    }

    /// <summary>贴图按需释放（插件卸载时调用）。</summary>
    public static void ClearCache()
    {
        foreach (var bitmap in Masks.Values) bitmap?.Dispose();
        Masks.Clear();
    }
}

/// <summary>把花火挂到屏幕上。</summary>
public static class HanabiOverlay
{
    private static HanabiSimulation? _simulation;
    private static HanabiOverlayWindow? _window;

    /// <summary>在控件上的 dotInAnchor 处放一朵花，铺满整块屏幕。</summary>
    public static void Play(Control anchor, Point dotInAnchor)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(anchor);
            if (topLevel == null)
            {
                GuardLog.Warn("[hanabi] 拿不到 TopLevel");
                return;
            }

            _simulation ??= CreateSimulation();
            if (_simulation is not { Ready: true }) return;

            // 指示点在屏幕上的位置 → 换成本屏归一化坐标，花就从绿点那儿开
            var point = anchor.PointToScreen(dotInAnchor);
            var screen = topLevel.Screens.ScreenFromPoint(point) ?? topLevel.Screens.Primary;
            var nx = 0.5;
            var ny = 0.5;
            if (screen is { Bounds.Width: > 0, Bounds.Height: > 0 })
            {
                nx = Math.Clamp((point.X - screen.Bounds.X) / (double)screen.Bounds.Width, 0.05, 0.95);
                ny = Math.Clamp((point.Y - screen.Bounds.Y) / (double)screen.Bounds.Height, 0.05, 0.95);
            }

            if (_window != null)
            {
                _window.Close();
                _window = null;
            }

            var window = new HanabiOverlayWindow(_simulation, nx, ny);
            window.Finished += () => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_window, window)) _window = null;
                window.Close();
            });
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_window, window)) _window = null;
            };

            _window = window;
            window.Show();
            GuardLog.Info($"[hanabi] 开花于屏幕 {nx:P0}, {ny:P0}");
        }
        catch (Exception ex)
        {
            GuardLog.Error("[hanabi] 播放失败", ex);
        }
    }

    private static HanabiSimulation CreateSimulation()
    {
        var simulation = new HanabiSimulation();
        simulation.Load();
        return simulation;
    }
}
