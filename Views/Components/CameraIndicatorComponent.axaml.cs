using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using MikuGuard.Models;
using MikuGuard.Services;

namespace MikuGuard.Views.Components;

/// <summary>
/// 摄像头安全指示器：摄像头被调用时闪出一个绿点，随后收缩成小圆点常亮。
/// </summary>
[ComponentInfo(
    "3F9C1D64-71B2-4A55-9C0E-2B8D5A6F1C71",
    "摄像头安全指示器",
    "\uE392",
    "摄像头被调用时显示指示点（颜色、图标可自定义）")]
public partial class CameraIndicatorComponent : ComponentBase<CameraIndicatorSettings>
{
    private CameraGuardService? _service;
    private DispatcherTimer? _collapseTimer;
    private DispatcherTimer? _pulseTimer;
    private DispatcherTimer? _hideTimer;
    private DispatcherTimer? _settingsRetry;
    private bool _applied;

    public CameraIndicatorComponent()
    {
        InitializeComponent();

        RootGrid.Transitions = new Transitions
        {
            new DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new QuadraticEaseOut() },
            new DoubleTransition { Property = HeightProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new QuadraticEaseOut() },
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(700), Easing = new SineEaseInOut() }
        };

        Dot.Transitions = new Transitions
        {
            new DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new QuadraticEaseOut() },
            new DoubleTransition { Property = HeightProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new QuadraticEaseOut() },
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(700), Easing = new SineEaseInOut() }
        };

        Ring.Transitions = new Transitions
        {
            new DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new QuadraticEaseOut() },
            new DoubleTransition { Property = HeightProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new QuadraticEaseOut() }
        };

        Icon.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new QuadraticEaseOut() }
        };

        // 组件本身始终占位，只隐藏内部图形，避免信息岛布局跳动
        RootGrid.IsVisible = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (Settings == null) StartSettingsRetry();
        ApplyAppearance();
        HookSettings();

        _service = IAppHost.TryGetService<CameraGuardService>();
        if (_service != null)
        {
            _service.PropertyChanged += OnServiceChanged;
            Refresh(_service.IsCameraInUse);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_service != null) _service.PropertyChanged -= OnServiceChanged;
        if (Settings != null) Settings.PropertyChanged -= OnSettingsChanged;
        _collapseTimer?.Stop();
        _pulseTimer?.Stop();
        _hideTimer?.Stop();
        _settingsRetry?.Stop();
    }

    /// <summary>宿主注入设置的时机不完全确定，拿不到就短轮询几次。</summary>
    private void StartSettingsRetry()
    {
        _settingsRetry?.Stop();
        var tries = 0;
        _settingsRetry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _settingsRetry.Tick += (_, _) =>
        {
            if (Settings != null || ++tries > 25)
            {
                _settingsRetry?.Stop();
                _settingsRetry = null;
                HookSettings();
                ApplyAppearance();
                if (_service != null) Refresh(_service.IsCameraInUse);
                return;
            }
        };
        _settingsRetry.Start();
    }

    private void HookSettings()
    {
        if (Settings == null) return;
        Settings.PropertyChanged -= OnSettingsChanged;
        Settings.PropertyChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(ApplyAppearance);
    }

    private void OnServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_service == null) return;

        if (e.PropertyName == nameof(CameraGuardService.IsCameraInUse))
            Dispatcher.UIThread.Post(() => Refresh(_service.IsCameraInUse));
    }

    // ================= 外观 =================

    private void ApplyAppearance()
    {
        if (Settings == null) return;

        var dotSize = Math.Clamp(Settings.DotSize, 4, 48);
        var expanded = Math.Clamp(Settings.ExpandedSize, dotSize, 72);
        Dot.Fill = new SolidColorBrush(Settings.DotColor);
        Icon.Foreground = new SolidColorBrush(Settings.IconColor);
        Icon.Glyph = string.IsNullOrEmpty(Settings.IconGlyph) ? "" : Settings.IconGlyph;
        Icon.IsVisible = !string.IsNullOrEmpty(Settings.IconGlyph) && _applied && RootGrid.Width > dotSize + 0.5;

        var ringBrush = new SolidColorBrush(Settings.RingColor);
        Ring.Stroke = ringBrush;
        Ring.IsVisible = ringBrush is ISolidColorBrush { Color.A: > 0 };

        if (!_applied)
        {
            RootGrid.Width = dotSize;
            RootGrid.Height = dotSize;
            Dot.Width = dotSize;
            Dot.Height = dotSize;
            Ring.Width = dotSize + 5;
            Ring.Height = dotSize + 5;
            Icon.FontSize = Math.Max(dotSize * 0.55, 9);
        }
        else if (_service == null || !_service.IsCameraInUse)
        {
            Collapse(dotSize);
        }

        _applied = true;
    }

    // ================= 状态 =================

    private void Refresh(bool inUse)
    {
        if (Settings == null) return;

        if (inUse)
        {
            RootGrid.IsVisible = true;
            Expand();
        }
        else
        {
            IdleOut();
        }
    }

    private void Expand()
    {
        if (Settings == null) return;

        _collapseTimer?.Stop();
        _hideTimer?.Stop();

        var expanded = Math.Clamp(Settings.ExpandedSize, Math.Clamp(Settings.DotSize, 4, 48), 72);
        var dotSize = Math.Clamp(Settings.DotSize, 4, 48);

        RootGrid.IsVisible = true;
        RootGrid.Opacity = 1;
        Dot.Opacity = 1;
        RootGrid.Width = expanded;
        RootGrid.Height = expanded;
        Dot.Width = expanded;
        Dot.Height = expanded;
        Ring.Width = expanded + 5;
        Ring.Height = expanded + 5;
        Icon.FontSize = Math.Max(expanded * 0.55, 9);
        Icon.Opacity = string.IsNullOrEmpty(Settings.IconGlyph) ? 0 : 1;
        Icon.IsVisible = !string.IsNullOrEmpty(Settings.IconGlyph);

        StartPulse();
        PlayRipple();

        if (Settings.ExpandHoldMs > 0)
        {
            _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Settings.ExpandHoldMs) };
            _collapseTimer.Tick += (_, _) =>
            {
                _collapseTimer?.Stop();
                Collapse(dotSize);
            };
            _collapseTimer.Start();
        }
    }

    /// <summary>从指示点向整个岛扩散一次 MD3 涟漪。</summary>
    private void PlayRipple()
    {
        if (Settings == null || !Settings.RippleEnabled) return;

        var center = new Avalonia.Point(Bounds.Width / 2, Bounds.Height / 2);
        IslandRippleHost.Play(this, center, Settings.RippleDurationMs, Settings.RippleStrength);
    }

    private void Collapse(double dotSize)
    {
        RootGrid.Width = dotSize;
        RootGrid.Height = dotSize;
        Dot.Width = dotSize;
        Dot.Height = dotSize;
        Ring.Width = dotSize + 5;
        Ring.Height = dotSize + 5;
        Icon.Opacity = 0;
        Icon.IsVisible = false;
    }

    private void IdleOut()
    {
        _collapseTimer?.Stop();
        StopPulse();

        if (!RootGrid.IsVisible) return;

        Dot.Opacity = 0;
        RootGrid.Opacity = 0;

        _hideTimer?.Stop();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer?.Stop();
            RootGrid.IsVisible = false;
            RootGrid.Opacity = 1;
            Dot.Opacity = 1;
            if (Settings != null)
            {
                var dotSize = Math.Clamp(Settings.DotSize, 4, 48);
                RootGrid.Width = dotSize;
                RootGrid.Height = dotSize;
                Dot.Width = dotSize;
                Dot.Height = dotSize;
                Ring.Width = dotSize + 5;
                Ring.Height = dotSize + 5;
                Icon.Opacity = 0;
                Icon.IsVisible = false;
            }
        };
        _hideTimer.Start();
    }

    private void StartPulse()
    {
        StopPulse();
        if (Settings == null || !Settings.Pulse) return;

        var dim = false;
        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(950) };
        _pulseTimer.Tick += (_, _) =>
        {
            dim = !dim;
            Dot.Opacity = dim ? 0.5 : 1;
            Ring.Opacity = dim ? 0.9 : 0.4;
        };
        _pulseTimer.Start();
    }

    private void StopPulse()
    {
        _pulseTimer?.Stop();
        _pulseTimer = null;
        Dot.Opacity = 1;
        Ring.Opacity = 1;
    }

}
