using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace MikuGuard.Services.Hanabi;

/// <summary>
/// 花火的渲染。原版是 D3D 逐 sprite 设置混合状态（星光/放射走 screen，圆环走加法），
/// 越叠越亮才有烟花的辉光感；Avalonia 的 DrawingContext 没有混合模式，
/// 所以主路径借 Skia 的 canvas 直接画，Avalonia 那套只作为降级。
/// </summary>
public static class HanabiRenderer
{
    private static bool? _skiaSupported;

    /// <summary>把当前时刻的整朵花画到 ctx 上（左上角为原点）。</summary>
    public static void Render(DrawingContext context, HanabiSimulation simulation, double now, Size size)
    {
        if (size.Width < 2 || size.Height < 2 || !simulation.Ready) return;

        if (SkiaSupported())
        {
            context.Custom(new HanabiPainter(simulation, now, size));
            return;
        }

        RenderFallback(context, simulation, now, size);
    }

    private static bool SkiaSupported()
    {
        if (_skiaSupported is { } known) return known;

        try
        {
            // 宿主跑的是 Skia 后端；这两步同时能验证 Avalonia.Skia 和 SkiaSharp 都在
            _skiaSupported = typeof(ISkiaSharpApiLeaseFeature).Assembly != null
                             && typeof(SKCanvas).Assembly != null;
        }
        catch
        {
            _skiaSupported = false;
        }

        if (_skiaSupported == false) GuardLog.Warn("[hanabi] 当前渲染后端不是 Skia，改用降级绘制");

        return _skiaSupported == true;
    }

    // ===================== 降级路径（纯 Avalonia 绘制） =====================

    private static void RenderFallback(DrawingContext context, HanabiSimulation simulation, double now, Size size)
    {
        var scale = Math.Max(size.Width, size.Height) / 1000.0;

        for (var layer = 0; layer < 3; layer++)
        {
            foreach (var burst in simulation.Bursts)
            {
                foreach (var particle in burst.Particles)
                {
                    var emitter = simulation.Emitters[particle.Emitter];
                    if (emitter.Layer != layer) continue;

                    if (!HanabiSimulation.Sample(emitter, particle, now, burst.Start, scale, out var sample)) continue;
                    if (sample.Diameter <= 0.5 || sample.A <= 0.004) continue;

                    var position = PositionOf(burst, particle, sample, size, scale);
                    var color = Color.FromRgb((byte)(sample.R * 255), (byte)(sample.G * 255), (byte)(sample.B * 255));

                    if (emitter.Material == 49)
                    {
                        FallbackRainbow(context, position.X, position.Y, sample);
                        continue;
                    }

                    var file = emitter.Material == 50 ? "ring_4862.png" : MaskName(emitter.Textures);
                    var bitmap = FallbackTexture(file);
                    if (bitmap == null) continue;

                    var rect = new Rect(position.X - sample.Diameter / 2, position.Y - sample.Diameter / 2,
                        sample.Diameter, sample.Diameter);

                    var matrix = Matrix.CreateTranslation(-position.X, -position.Y)
                                 * Matrix.CreateRotation(sample.Rotation)
                                 * Matrix.CreateTranslation(position.X, position.Y);

                    using (context.PushTransform(matrix))
                    using (context.PushOpacity(Math.Clamp(sample.A, 0, 1)))
                    {
                        if (emitter.Material == 50)
                        {
                            context.DrawImage(bitmap, rect);
                        }
                        else
                        {
                            var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
                            using (context.PushOpacityMask(brush, rect))
                                context.FillRectangle(new SolidColorBrush(color), rect);
                        }
                    }
                }
            }
        }
    }

    /// <summary>降级路径的彩虹光环：实心圆 + 锥形渐变（精确的扇形分割要 Skia 才做得到）。</summary>
    private static void FallbackRainbow(DrawingContext context, double x, double y, in HanabiSample sample)
    {
        var radius = sample.Diameter / 2;
        if (radius < 2) return;

        using (context.PushOpacity(Math.Clamp(sample.A * 0.75, 0, 1)))
        {
            context.DrawEllipse(RainbowBrush(), null, new Point(x, y), radius, radius);
        }
    }

    // ===================== 共用 =====================

    // 彩虹光环的调色板（取自原 shader 的 cb0[6..10]），按角度循环上色
    internal static readonly SKColor[] RainbowPalette =
    {
        new(237, 58, 156),    // 粉
        new(255, 90, 18),     // 橙
        new(255, 216, 0),     // 黄
        new(184, 210, 0),     // 黄绿
        new(0, 160, 233)      // 蓝
    };

    /// <summary>绕一圈重复几个色环（shader 里是 angle*3）。</summary>
    internal const int RainbowCycles = 3;

    private static ConicGradientBrush RainbowBrush() => new()
    {
        Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(255, 237, 58, 156), 0.00),
            new GradientStop(Color.FromArgb(255, 255, 90, 18), 0.15),
            new GradientStop(Color.FromArgb(255, 255, 216, 0), 0.31),
            new GradientStop(Color.FromArgb(255, 184, 210, 0), 0.46),
            new GradientStop(Color.FromArgb(255, 0, 160, 233), 0.62),
            new GradientStop(Color.FromArgb(255, 120, 60, 220), 0.78),
            new GradientStop(Color.FromArgb(255, 237, 58, 156), 0.92),
            new GradientStop(Color.FromArgb(255, 237, 58, 156), 1.00)
        }
    };

    internal static Point PositionOf(HanabiBurst burst, HanabiParticle particle, in HanabiSample sample,
        Size size, double scale) => new(
        size.Width * burst.OriginX + Math.Cos(particle.Angle) * sample.Distance * scale,
        size.Height * burst.OriginY - Math.Sin(particle.Angle) * sample.Distance * scale);

    internal static string? MaskName(string[] textures)
    {
        if (textures.Length == 0) return null;
        return textures[0] switch
        {
            "maimai_1519.png" => "mask_1519.png",
            "maimai_2694.png" => "mask_2694.png",
            "maimai_3691.png" => "ring_4862.png",
            "maimai_4862.png" => "ring_4862.png",
            _ => null
        };
    }

    internal static string? TexturePath(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        var path = MikuGuardPaths.Resolve(Path.Combine("Assets", "Hanabi", fileName));
        if (File.Exists(path)) return path;
        GuardLog.Warn($"[hanabi] 缺少贴图：{path}");
        return null;
    }

    // 降级路径用的 Avalonia 贴图
    private static readonly Dictionary<string, Bitmap?> FallbackCache = new(StringComparer.OrdinalIgnoreCase);

    private static Bitmap? FallbackTexture(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        if (FallbackCache.TryGetValue(fileName, out var cached)) return cached;

        try
        {
            var path = TexturePath(fileName);
            var bitmap = path == null ? null : new Bitmap(path);
            FallbackCache[fileName] = bitmap;
            return bitmap;
        }
        catch (Exception ex)
        {
            GuardLog.Error($"[hanabi] 贴图加载失败：{fileName}", ex);
            FallbackCache[fileName] = null;
            return null;
        }
    }

    /// <summary>释放贴图缓存（插件卸载时）。</summary>
    public static void ClearCache()
    {
        foreach (var bitmap in FallbackCache.Values) bitmap?.Dispose();
        FallbackCache.Clear();
        HanabiPainter.ClearTextures();
    }
}

/// <summary>
/// 真正画烟花的那一层：拿 Skia 的 canvas，按原版的三层混合顺序逐 sprite 画。
/// </summary>
internal sealed class HanabiPainter : ICustomDrawOperation
{
    private static readonly Dictionary<string, SKImage?> Textures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<uint, SKColorFilter> Filters = new();

    // 扇形的硬边界渐变：调色板绕一圈重复 RainbowCycles 次，色块之间不留过渡
    private static readonly SKColor[] SectorColors;
    private static readonly float[] SectorPositions;

    static HanabiPainter()
    {
        var palette = HanabiRenderer.RainbowPalette;
        var sectors = palette.Length * HanabiRenderer.RainbowCycles;
        var colors = new List<SKColor>(sectors * 2 + 1);
        var positions = new List<float>(sectors * 2 + 1);

        for (var i = 0; i < sectors; i++)
        {
            var color = palette[i % palette.Length];
            var start = (float)i / sectors;
            var end = (float)(i + 1) / sectors;

            positions.Add(start);
            colors.Add(color);
            positions.Add(Math.Max(start, end - 0.0008f));
            colors.Add(color);
        }

        positions.Add(1f);
        colors.Add(palette[0]);

        SectorColors = colors.ToArray();
        SectorPositions = positions.ToArray();
    }

    private readonly HanabiSimulation _sim;
    private readonly double _now;
    private readonly Size _size;

    public HanabiPainter(HanabiSimulation sim, double now, Size size)
    {
        _sim = sim;
        _now = now;
        _size = size;
    }

    public Rect Bounds => new(_size);

    public bool HitTest(Point p) => false;

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Dispose()
    {
    }

    public void Render(ImmediateDrawingContext context)
    {
        var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (feature == null)
        {
            GuardLog.Warn("[hanabi] 拿不到 Skia 画布");
            return;
        }

        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;
        var scale = Math.Max(_size.Width, _size.Height) / 1000.0;

        using var paint = new SKPaint { IsAntialias = true };

        for (var layer = 0; layer < 3; layer++)
        {
            foreach (var burst in _sim.Bursts)
            {
                foreach (var particle in burst.Particles)
                {
                    var emitter = _sim.Emitters[particle.Emitter];
                    if (emitter.Layer != layer) continue;

                    if (!HanabiSimulation.Sample(emitter, particle, _now, burst.Start, scale, out var sample)) continue;
                    if (sample.Diameter <= 0.5 || sample.A <= 0.004) continue;

                    var position = HanabiRenderer.PositionOf(burst, particle, sample, _size, scale);
                    var x = (float)position.X;
                    var y = (float)position.Y;
                    var radius = (float)(sample.Diameter / 2);

                    if (emitter.Material == 49)
                    {
                        DrawRainbow(canvas, paint, x, y, radius, sample);
                        continue;
                    }

                    var file = emitter.Material == 50 ? "ring_4862.png" : HanabiRenderer.MaskName(emitter.Textures);
                    var image = Texture(file);
                    if (image == null) continue;

                    paint.Reset();
                    paint.IsAntialias = true;
                    paint.Color = SKColors.White.WithAlpha((byte)(Math.Clamp(sample.A, 0, 1) * 255));

                    if (emitter.Material == 50)
                    {
                        // 圆环是加法混合：越叠越亮
                        paint.BlendMode = SKBlendMode.Plus;
                    }
                    else
                    {
                        // 星光 / 放射是 screen：黑底不会压暗背景
                        paint.BlendMode = SKBlendMode.Screen;
                        paint.ColorFilter = Tint(emitter.Material == 80
                            ? SKColors.White
                            : new SKColor((byte)(sample.R * 255), (byte)(sample.G * 255), (byte)(sample.B * 255)));
                    }

                    canvas.Save();
                    canvas.RotateDegrees((float)(sample.Rotation * 180 / Math.PI), x, y);
                    canvas.DrawImage(image, new SKRect(0, 0, image.Width, image.Height),
                        new SKRect(x - radius, y - radius, x + radius, y + radius), paint);
                    canvas.Restore();
                }
            }
        }
    }

    /// <summary>
    /// 彩虹光环：**按角度上色的实心扇形盘**（原先我理解成一圈圈的环，是错的）。
    /// 原 shader 里 hue 来自 atan2 出来的角度、乘 3 后取 frac，再用阈值从 5 个调色板颜色里挑一个，
    /// 所以是一圈硬边的彩色扇形；亮度从圆心往外衰减，整圈还会按角速度转。
    /// </summary>
    private static void DrawRainbow(SKCanvas canvas, SKPaint paint, float x, float y, float radius,
        in HanabiSample sample)
    {
        if (radius < 2) return;

        var alpha = (float)Math.Clamp(sample.A * 0.75, 0, 1);
        if (alpha <= 0.004) return;

        // 整盘做成一层，再用径向渐变做 DstIn 遮罩，得到「中心亮、边缘散开」的扇形
        var save = canvas.SaveLayer();
        canvas.Translate(x, y);
        // 扇形跟着粒子的角速度转（原版 angularVelocity 就是这么用的）
        canvas.RotateDegrees((float)(sample.Rotation * 180 / Math.PI));

        using (var sweep = SKShader.CreateSweepGradient(SKPoint.Empty, SectorColors, SectorPositions, SKMatrix.Identity))
        using (var colorPaint = new SKPaint
               {
                   IsAntialias = true,
                   Style = SKPaintStyle.Fill,
                   Shader = sweep,
                   BlendMode = SKBlendMode.SrcOver,
                   Color = SKColors.White.WithAlpha((byte)(alpha * 255))
               })
        {
            canvas.DrawCircle(0, 0, radius, colorPaint);
        }

        // 径向遮罩：半径 55% 以内保持不动，之后渐隐到 0
        using (var fade = SKShader.CreateRadialGradient(SKPoint.Empty, radius,
                   new[]
                   {
                       SKColors.White, SKColors.White,
                       new SKColor(255, 255, 255, 120),
                       new SKColor(255, 255, 255, 0)
                   },
                   new[] { 0f, 0.55f, 0.82f, 1f }, SKShaderTileMode.Clamp))
        using (var maskPaint = new SKPaint
               {
                   IsAntialias = true,
                   Style = SKPaintStyle.Fill,
                   Shader = fade,
                   BlendMode = SKBlendMode.DstIn
               })
        {
            canvas.DrawRect(new SKRect(-radius, -radius, radius, radius), maskPaint);
        }

        canvas.RestoreToCount(save);
    }

    /// <summary>用一个颜色去乘贴图（等价于 shader 里的 rgb *= tint）。</summary>
    private static SKColorFilter Tint(SKColor color)
    {
        var key = (uint)color;
        if (Filters.TryGetValue(key, out var cached)) return cached;

        var filter = SKColorFilter.CreateBlendMode(color, SKBlendMode.Modulate);
        if (Filters.Count > 4096) Filters.Clear();
        Filters[key] = filter;
        return filter;
    }

    private static SKImage? Texture(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        if (Textures.TryGetValue(fileName, out var cached)) return cached;

        try
        {
            var path = HanabiRenderer.TexturePath(fileName);
            SKImage? image = null;
            if (path != null)
            {
                using var bitmap = SKBitmap.Decode(path);
                if (bitmap != null) image = SKImage.FromBitmap(bitmap);
            }

            Textures[fileName] = image;
            return image;
        }
        catch (Exception ex)
        {
            GuardLog.Error($"[hanabi] 贴图加载失败：{fileName}", ex);
            Textures[fileName] = null;
            return null;
        }
    }

    internal static void ClearTextures()
    {
        foreach (var image in Textures.Values) image?.Dispose();
        Textures.Clear();
        foreach (var filter in Filters.Values) filter.Dispose();
        Filters.Clear();
    }
}
