using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using ClassIsland.Core.Assists;
using MikuGuard.Views.Components;

namespace MikuGuard.Services;

/// <summary>
/// 把涟漪画到「岛」上（照 ClassIslandInjector 的口径）：
/// 岛 = 名为 BackgroundBorder 的行背景 Border，涟漪画进 WindowRoot，
/// 全程只用 TranslatePoint 换算渲染坐标——
/// 注意主界面套了 ZoomBorder/缩放变换，控件的 Bounds 是缩放前的布局尺寸，
/// 拿它当渲染尺寸用就会偏到岛外（踩过：GridRoot 布局 1130x40，渲染出来是 1920x68）。
/// </summary>
public static class IslandRippleHost
{
    /// <summary>在岛上播放一次涟漪。anchor 是发出涟漪的控件（指示点所在组件）。</summary>
    public static void Play(Control anchor, Point dotCenterInAnchor, double durationMs, double strength)
    {
        try
        {
            if (TopLevel.GetTopLevel(anchor) is not Window window)
            {
                GuardLog.Warn("[ripple] 拿不到所在窗口");
                return;
            }

            if (window.Content is not Panel root)
            {
                GuardLog.Warn($"[ripple] 窗口内容不是 Panel：{window.Content?.GetType().Name}");
                return;
            }

            // 1) 圆心：指示点在 WindowRoot 坐标系里的位置（先算，选岛要用它）
            if (anchor.TranslatePoint(dotCenterInAnchor, root) is not { } dotInRoot)
            {
                GuardLog.Warn("[ripple] 算不出指示点位置");
                return;
            }

            // 2) 找到包含指示点的那块岛（行背景 Border 是背景层，不在组件的祖先链上，
            //    必须在窗口里搜；多行主界面时按"包含指示点"挑出正确的那一块）
            var island = FindIslandBorder(anchor, root, dotInRoot) ?? FindGridRoot(anchor);
            if (island == null)
            {
                GuardLog.Warn("[ripple] 没找到岛");
                return;
            }

            // 3) 量出岛在 WindowRoot 坐标系里的渲染矩形（含缩放）
            var rect = MeasureInRoot(island, root);
            if (rect.Width < 2 || rect.Height < 2)
            {
                GuardLog.Warn($"[ripple] 岛渲染矩形异常：{rect}");
                return;
            }

            var center = new Point(
                Math.Clamp(dotInRoot.X, rect.X, rect.Right),
                Math.Clamp(dotInRoot.Y, rect.Y, rect.Bottom));

            // 覆盖整块岛：从圆心到最远角的距离
            var maxRadius = Math.Max(
                Math.Sqrt(Math.Pow(Math.Max(center.X - rect.X, rect.Right - center.X), 2) +
                          Math.Pow(Math.Max(center.Y - rect.Y, rect.Bottom - center.Y), 2)), 1);

            var ripple = new IslandRipple(center, maxRadius, ThemeAccent.GetAccentColor(),
                durationMs, strength, rect, ReadCornerRadius(island, rect))
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };

            ripple.Completed += r => root.Children.Remove(r);
            root.Children.Add(ripple);
            GuardLog.Info(
                $"[ripple] 已挂载：岛渲染矩形 {rect.X:F0},{rect.Y:F0} - {rect.Right:F0},{rect.Bottom:F0} " +
                $"({rect.Width:F0}x{rect.Height:F0})，圆心({center.X:F0},{center.Y:F0})，最大半径 {maxRadius:F0}");
        }
        catch (Exception ex)
        {
            GuardLog.Error("[ripple] 播放失败", ex);
        }
    }

    /// <summary>把控件在 WindowRoot 坐标系里的渲染矩形量出来（对角两点各 TranslatePoint 一次）。</summary>
    private static Rect MeasureInRoot(Control element, Panel root)
    {
        var tl = element.TranslatePoint(default, root);
        var br = element.TranslatePoint(new Point(element.Bounds.Width, element.Bounds.Height), root);
        if (tl == null || br == null) return default;
        return new Rect(tl.Value, br.Value);
    }

    /// <summary>
    /// 在窗口里找岛（Injector 契约里的 BackgroundBorder）。行背景是画在内容底下的背景层，
    /// 不在组件的祖先链上，只能按名字搜；多行主界面时选渲染矩形包含（或最靠近）指示点的那块。
    /// </summary>
    private static Control? FindIslandBorder(Control anchor, Panel root, Point dotInRoot)
    {
        if (TopLevel.GetTopLevel(anchor) is not Window window) return null;

        var borders = window.GetVisualDescendants()
            .OfType<Border>()
            .Where(x => x.Name == "BackgroundBorder" && x.IsVisible && x.Bounds.Width > 1 && x.Bounds.Height > 1)
            .Select(b => (Border: b, Rect: MeasureInRoot(b, root)))
            .Where(x => x.Rect.Width > 1 && x.Rect.Height > 1)
            .ToList();

        if (borders.Count == 0) return null;

        // 多行主界面时选渲染矩形包含（或最靠近）指示点的那一块
        return borders
            .OrderBy(x => Distance(x.Rect, dotInRoot))
            .First().Border;
    }

    private static double Distance(Rect r, Point p)
    {
        var dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
        var dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Control? FindGridRoot(Visual? start)
    {
        for (var v = start; v != null; v = v.GetVisualParent())
        {
            if (v is Control { Name: "GridRoot" } grid) return grid;
        }

        return null;
    }

    /// <summary>
    /// 岛的圆角。附加属性里存的是缩放前的布局值，渲染时要乘上缩放系数
    /// （渲染高 / 布局高），否则裁剪圆角和肉眼看到的岛对不上。
    /// </summary>
    private static double ReadCornerRadius(Control island, Rect renderedRect)
    {
        double layoutRadius;
        try
        {
            layoutRadius = MainWindowStylesAssist.GetCornerRadius(island);
        }
        catch
        {
            layoutRadius = 0;
        }

        if (layoutRadius <= 0) return Math.Min(renderedRect.Height / 2, 24);

        var factor = island.Bounds.Height > 1 ? renderedRect.Height / island.Bounds.Height : 1;
        return Math.Clamp(layoutRadius * factor, 0, renderedRect.Height / 2);
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
