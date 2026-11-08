using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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
        HanabiRenderer.Render(context, _sim, Now, Bounds.Size);
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
                // 指示点常在屏幕角落（信息岛在最上面），花心正好压边的话大半朵会被裁掉，
                // 所以往里收一点：仍然是从指示点那个方向开出来的。
                nx = Math.Clamp((point.X - screen.Bounds.X) / (double)screen.Bounds.Width, 0.15, 0.85);
                ny = Math.Clamp((point.Y - screen.Bounds.Y) / (double)screen.Bounds.Height, 0.15, 0.85);
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
