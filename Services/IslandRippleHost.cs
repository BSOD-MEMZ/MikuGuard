using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using ClassIsland.Core.Assists;
using MikuGuard.Views.Components;

namespace MikuGuard.Services;

/// <summary>
/// 把涟漪画到「岛」上：找到宿主主界面里装岛的那层容器（GridRoot），
/// 在它上层叠一个与岛等大、按岛圆角裁剪的画布，涟漪从指示点扩散到整个岛。
/// </summary>
public static class IslandRippleHost
{
    private static readonly Dictionary<Window, LayerInfo> Layers = new();

    private sealed record LayerInfo(Panel Root, Control Island);

    /// <summary>在岛上播放一次涟漪。anchor 是发出涟漪的控件（指示点所在组件）。</summary>
    public static void Play(Control anchor, Point dotCenterInAnchor, double durationMs, double strength)
    {
        try
        {
            if (TopLevel.GetTopLevel(anchor) is not Window window) return;
            if (window.Content is not Panel root) return;

            var layer = ResolveLayer(window, anchor, root);
            if (layer == null) return;

            var island = layer.Island;
            var offset = island.TranslatePoint(default, root) ?? default;
            var size = island.Bounds.Size;
            if (size.Width < 2 || size.Height < 2) return;

            var center = anchor.TranslatePoint(dotCenterInAnchor, island) ?? dotCenterInAnchor;
            center = new Point(
                Math.Clamp(center.X, 0, size.Width),
                Math.Clamp(center.Y, 0, size.Height));

            // 要覆盖整块岛：从中心到最远角的距离
            var maxRadius = Math.Max(
                Math.Sqrt(Math.Pow(Math.Max(center.X, size.Width - center.X), 2) +
                          Math.Pow(Math.Max(center.Y, size.Height - center.Y), 2)), 1);

            var ripple = new IslandRipple(center, maxRadius, ThemeAccent.GetAccentColor(),
                durationMs, strength, ReadCornerRadius(island))
            {
                Width = size.Width,
                Height = size.Height,
                Margin = new Thickness(offset.X, offset.Y, 0, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                IsHitTestVisible = false
            };

            ripple.Completed += r => root.Children.Remove(r);
            root.Children.Add(ripple);
        }
        catch (Exception ex)
        {
            GuardLog.Warn($"[ripple] 播放失败：{ex.Message}");
        }
    }

    private static LayerInfo? ResolveLayer(Window window, Control anchor, Panel root)
    {
        if (Layers.TryGetValue(window, out var cached) && cached.Island.IsAttachedToVisualTree())
            return cached;

        var island = FindIsland(anchor) ?? FindIslandByType(root);
        if (island == null) return null;

        var info = new LayerInfo(root, island);
        Layers[window] = info;
        return info;
    }

    /// <summary>从组件往上找宿主装岛的 GridRoot。</summary>
    private static Control? FindIsland(Visual? start)
    {
        for (var v = start; v != null; v = v.GetVisualParent())
        {
            if (v is Control { Name: "GridRoot" } grid) return grid;
        }

        return null;
    }

    /// <summary>兜底：按控件类型名在窗口里搜一遍。</summary>
    private static Control? FindIslandByType(Visual root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is Control { Name: "GridRoot" } grid) return grid;
            var found = FindIslandByType(child);
            if (found != null) return found;
        }

        return null;
    }

    /// <summary>读岛的实际圆角（宿主把 MainWindowStylesAssist.CornerRadius 绑在 GridRoot 上）。</summary>
    private static double ReadCornerRadius(Control island)
    {
        try
        {
            var radius = MainWindowStylesAssist.GetCornerRadius(island);
            if (radius > 0) return radius;
        }
        catch
        {
            // 宿主版本对不上就算了
        }

        // 兜底：按胶囊形状估算
        return Math.Min(island.Bounds.Height / 2, 20);
    }
}

/// <summary>取 ClassIsland 当前主题色。</summary>
public static class ThemeAccent
{
    private static readonly Color Fallback = Color.Parse("#FF4CA3F0");

    public static Color GetAccentColor()
    {
        try
        {
            var app = Application.Current;
            if (app == null) return Fallback;

            foreach (var key in new[]
                     {
                         "SystemAccentColor", "SystemAccentColorPrimary", "SystemAccentColorDark1",
                         "SystemControlHighlightAccentBrush", "ThemeAccentColor"
                     })
            {
                if (!app.TryGetResource(key, null, out var value)) continue;
                switch (value)
                {
                    case Color color:
                        return color;
                    case ISolidColorBrush brush:
                        return brush.Color;
                }
            }

            foreach (var style in app.Styles)
            {
                if (style?.GetType().Name != "FluentAvaloniaTheme") continue;
                var prop = style.GetType().GetProperty("CustomAccentColor");
                if (prop?.GetValue(style) is Color custom) return custom;
            }
        }
        catch (Exception ex)
        {
            GuardLog.Warn($"[ripple] 取主题色失败：{ex.Message}");
        }

        return Fallback;
    }
}
