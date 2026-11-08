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
                        DrawRainbow(canvas, x, y, radius, sample);
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
                        // 放射光芒（material 80）也是按粒子颜色染的（红→黄绿的随机插值），
                        // 之前这里写死成白色，尖刺就全白了
                        paint.ColorFilter = Tint(new SKColor(
                            (byte)(sample.R * 255), (byte)(sample.G * 255), (byte)(sample.B * 255)));
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

    // ===================== 彩虹光环：assets/rainbow.hlsl 的逐行 SkSL 移植 =====================
    //
    // 这层不能用「同心环」「扇形盘」之类的近似——它同时叠了三样东西：
    //   1. 按角度的色带（5 色 + 灰，用 9t/5 的步进随��间滚动）
    //   2. 屏幕空间的网点（halftone），这就是官方截图里色块上的颗粒感
    //   3. 半径方向的环带调制，再套圆形遮罩 + 径向衰减
    // 原文件是反编译出来的 HLSL，这里一条一条照抄成 SkSL，交给 Skia 的 runtime effect 逐像素跑。

    // ---------------- 彩虹光环 ----------------
    //
    // 原版是 assets/rainbow.hlsl（逐像素的 fragment shader，反编译出来的）。我把它整段翻成 SkSL、
    // 用 SKRuntimeEffect 跑——结果这个 Skia 构建里 runtime effect 一进 DrawRect 就原生崩溃
    // （连 hello-world 级的 shader 也崩），这条路走不通。
    // 所以改成用 Skia 的常规原语，按它的公式重新拼：
    //   · 色带：hue = fract(角度×3/(2π) + floor(9t)/5)，用阈值 0.2/0.4/0.6/0.8 分 5 段上色
    //     → 每帧按 floor(9t)/5 平移 sweep 渐变的 stop 位置，色块就会一圈圈地转
    //   · 网点：shader 里那个 64 分格的屏幕空间圆点 → 平铺的点阵贴图，DstIn 叠上去
    //   · 径向：fract(径向×4－2(1－透明度)) 的环带 + 圆形遮罩 + 外缘衰减
    //   · 整体 ×0.75 不透明度
    private static SKBitmap? _dotTile;

    private static SKBitmap DotTile()
    {
        if (_dotTile != null) return _dotTile;

        const int n = 32;
        var tile = new SKBitmap(n, n);
        using (var canvas = new SKCanvas(tile))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Clear(SKColors.Transparent);
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    var dx = (x + 0.5) / n - 0.5;
                    var dy = (y + 0.5) / n - 0.5;
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    var v = 1.0 - d * 2.0;
                    v = v <= 0 ? 0 : v * v;
                    // 网点是「乘」在颜色上做明暗调制（不是加光）：最暗压到 55%，别再往下就是挖洞了
                    var g = (byte)(140 + v * 115);
                    tile.SetPixel(x, y, new SKColor(g, g, g, 255));
                }
            }
        }

        _dotTile = tile;
        return tile;
    }

    /// <summary>往白色里揉一点，得到官方那种淡彩观感。</summary>
    private static SKColor Pastel(SKColor color, float amount = 0.18f)
    {
        byte Mix(byte v) => (byte)(v + (255 - v) * amount);
        return new SKColor(Mix(color.Red), Mix(color.Green), Mix(color.Blue), color.Alpha);
    }

    /// <summary>按 shader 的分带规则，算出 sweep 渐变（色带会随时间步进平移）。</summary>
    private static SKColor[] BuildSectorColors(double time, out float[] positions)
    {
        // 按 shader 的阈值链推出来的真实分段：
        //   step(1.0, y) 恒为 0 → 第二段把颜色覆盖成 cb7(蓝)
        //   剩下的阈值是 0.2 / 0.4 / 0.6，所以最后那段是双倍宽（0.6~1.0）
        //   cb5(灰) 和 cb6(粉) 都会被后面覆盖掉，实际不出现——官方图里的粉色是另一层的色轮给的
        var palette = new[]
        {
            Pastel(new SKColor(255, 90, 18)),    // 橙   y < 0.2
            Pastel(new SKColor(255, 216, 0)),    // 黄   y < 0.4
            Pastel(new SKColor(184, 210, 0)),    // 黄绿 y < 0.6
            Pastel(new SKColor(0, 160, 233))     // 蓝   y < 1.0（双宽）
        };

        // shader：y = fract(角度×3/(2π) + offset)，offset = floor(9t)/5
        var offset = (float)(Math.Floor(9.0 * time) / 5.0 % 1.0);
        const int cycles = 3;
        const int steps = 720;

        var colors = new List<SKColor>();
        var pos = new List<float>();

        // 官方渲染里是「彩条 / 灰白条」交替：灰白来自 shader 的 cb5(0.5,0.5,0.5)，
        // 也就是说每段色带的后半截被灰白顶掉。这里就按 24 条（12 段 × 彩+灰）来切。
        var gray = new SKColor(190, 190, 190);
        const int stripes = 24;

        for (var i = 0; i < stripes; i++)
        {
            var u0 = (float)i / stripes;
            var u1 = (float)(i + 1) / stripes;

            SKColor color;
            if (i % 2 == 1)
            {
                color = gray;
            }
            else
            {
                var y = u0 * cycles + offset;
                y -= (float)Math.Floor(y);
                var band = y < 0.2f ? 0 : y < 0.4f ? 1 : y < 0.6f ? 2 : 3;
                color = palette[band];
            }

            colors.Add(color);
            pos.Add(u0);
            colors.Add(color);
            pos.Add(Math.Max(u0, u1 - 0.0006f));
        }

        colors.Add(colors[0]);
        pos.Add(1f);
        positions = pos.ToArray();
        return colors.ToArray();
    }

    /// <summary>彩虹光环：色带 + 网点 + 圆形遮罩 + 径向衰减。</summary>
    private void DrawRainbow(SKCanvas canvas, float x, float y, float radius, in HanabiSample sample)
    {
        if (radius < 2) return;

        // shader 里最终是 outA = a1 * 0.75，而且 a1 只在中段接近 1；
        // 这里再压一档——彩虹盘如果太实，screen 叠上去的放射光芒就完全看不出来了
        var alpha = (float)Math.Clamp(sample.A * 0.85, 0, 1);
        if (alpha <= 0.004) return;

        var colors = BuildSectorColors(_now, out var positions);

        var outer = canvas.SaveLayer();
        canvas.Translate(x, y);
        canvas.RotateDegrees((float)(sample.Rotation * 180 / Math.PI));

        // 圆形遮罩（shader 里是靠 step(length(uv-.5), .5) 限制的）
        using (var clip = new SKPath())
        {
            clip.AddCircle(0, 0, radius);
            canvas.ClipPath(clip, SKClipOperation.Intersect, true);
        }

        // 1) 色带：只把这一层做模糊（官方的楔形边缘是柔的），网点要留清晰，所以分开两层
        var blur = Math.Max(0.8f, radius * 0.004f);
        using (var layerPaint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(blur, blur) })
        {
            var inner = canvas.SaveLayer(layerPaint);
            using var sweep = SKShader.CreateSweepGradient(SKPoint.Empty, colors, positions, SKMatrix.Identity);
            using var sectorPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Shader = sweep,
                BlendMode = SKBlendMode.SrcOver,
                Color = SKColors.White.WithAlpha((byte)(alpha * 255))
            };
            canvas.DrawRect(new SKRect(-radius, -radius, radius, radius), sectorPaint);
            canvas.RestoreToCount(inner);
        }

        // 2) 网点（屏幕空间 128 格），只提亮不挖洞
        var cell = (float)(_size.Width / 128.0);
        using (var dot = SKShader.CreateBitmap(DotTile(), SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
                   SKMatrix.CreateScale(cell / 32f, cell / 32f)))
        using (var dotPaint = new SKPaint { Shader = dot, BlendMode = SKBlendMode.Modulate })
        {
            canvas.DrawRect(new SKRect(-radius, -radius, radius, radius), dotPaint);
        }

        // 3) 径向衰减
        using (var fade = SKShader.CreateRadialGradient(SKPoint.Empty, radius,
                   new[] { SKColors.White, SKColors.White, new SKColor(255, 255, 255, 150), new SKColor(255, 255, 255, 0) },
                   new[] { 0f, 0.82f, 0.95f, 1f }, SKShaderTileMode.Clamp))
        using (var fadePaint = new SKPaint { Shader = fade, BlendMode = SKBlendMode.DstIn })
        {
            canvas.DrawRect(new SKRect(-radius, -radius, radius, radius), fadePaint);
        }

        canvas.RestoreToCount(outer);
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
