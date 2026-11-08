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
                        FallbackRainbowRing(context, position.X, position.Y, sample);
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

    private static void FallbackRainbowRing(DrawingContext context, double x, double y, in HanabiSample sample)
    {
        var radius = sample.Diameter / 2;
        if (radius < 2) return;

        var brush = RainbowBrush();

        using (context.PushOpacity(Math.Clamp(sample.A * 0.75, 0, 1)))
        {
            foreach (var (bandScale, thickness, bandAlpha) in Bands)
            {
                using (context.PushOpacity(bandAlpha))
                {
                    var pen = new Pen(brush, Math.Max(1.5, radius * thickness)) { LineCap = PenLineCap.Flat };
                    context.DrawEllipse(null, pen, new Point(x, y), radius * bandScale, radius * bandScale);
                }
            }
        }
    }

    // ===================== 共用 =====================

    // 半径系数 / 线宽系数 / 该圈的不透明度。原版 shader 是 0.5~0.8 半径上一圈圈细条纹，
    // 越往外越淡；用几根细环近似，别做成三根粗线（那看着像靶子）。
    internal static readonly (double Scale, double Thickness, double Alpha)[] Bands =
    {
        (0.48, 0.013, 0.95),
        (0.55, 0.011, 0.72),
        (0.62, 0.012, 0.54),
        (0.70, 0.010, 0.38),
        (0.78, 0.011, 0.24)
    };

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

    private static readonly SKColor[] RainbowColors =
    {
        new(237, 58, 156), new(255, 90, 18), new(255, 216, 0), new(184, 210, 0),
        new(0, 160, 233), new(120, 60, 220), new(237, 58, 156)
    };

    private static readonly float[] RainbowPositions = { 0.00f, 0.15f, 0.31f, 0.46f, 0.62f, 0.78f, 0.92f };

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
        using var shader = SKShader.CreateSweepGradient(SKPoint.Empty, RainbowColors, RainbowPositions);

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
                        DrawRainbow(canvas, paint, shader, x, y, radius, sample);
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

    private static void DrawRainbow(SKCanvas canvas, SKPaint paint, SKShader shader, float x, float y,
        float radius, in HanabiSample sample)
    {
        paint.Reset();
        paint.IsAntialias = true;
        paint.Shader = shader;
        paint.BlendMode = SKBlendMode.SrcOver;
        paint.Style = SKPaintStyle.Stroke;

        var alpha = (byte)(Math.Clamp(sample.A * 0.75, 0, 1) * 255);

        canvas.Save();
        canvas.Translate(x, y);
        // shader 是原点做的扫掠渐变，平移后中心正好落在粒子位置
        for (var i = 0; i < HanabiRenderer.Bands.Length; i++)
        {
            var (bandScale, thickness, bandAlpha) = HanabiRenderer.Bands[i];
            paint.StrokeWidth = Math.Max(1.5f, radius * (float)thickness);
            paint.Color = SKColors.White.WithAlpha((byte)(alpha * bandAlpha));
            canvas.DrawCircle(0, 0, radius * (float)bandScale, paint);
        }

        canvas.Restore();
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
