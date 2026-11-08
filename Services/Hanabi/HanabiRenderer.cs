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
    private static readonly Dictionary<(uint, float), SKColorFilter> ScaledFilters = new();

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
                        DrawRainbow(canvas, x, y, (float)sample.Diameter, sample);
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
                        // 圆环是加法混合：越叠越亮。exp2(exposure) 已经烘进 ring_4862.png，
                        // tint 是白的，所以这里不用再挂颜色滤镜
                        paint.BlendMode = SKBlendMode.Plus;
                    }
                    else
                    {
                        // 星光 / 放射是 screen：黑底不会压暗背景
                        paint.BlendMode = SKBlendMode.Screen;
                        // rgb = 贴图 × tint × 2^exposure（原版 SpritePixel 里曝光就乘在这一步）
                        paint.ColorFilter = TintScaled(
                            new SKColor((byte)(sample.R * 255), (byte)(sample.G * 255), (byte)(sample.B * 255)),
                            MathF.Pow(2f, (float)emitter.Exposure));
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

    // ===================== 彩虹光环：assets/rainbow.hlsl 的逐行移植 =====================
    //
    // 这一层不再用「色带 + 网点」之类的近似，而是照原 shader 的公式逐像素算。
    // 原文件是反编译出来的 HLSL，下面每条结论都能在代码里直接读到（不是看图的观感）：
    //
    //   · 角度先被拆成三份（全部 frac）：
    //       aY = frac(角×3/2π)       —— 挑颜色
    //       aZ = frac(角×15/2π)      —— aZ<=0.5 的那一半是「灰」
    //       aX = frac(角×15/2π+0.03) —— aX<=0.56 的那一半不打网点
    //     所以绕一圈是 15 个彩扇 + 15 个灰扇交替（每个 12°）。
    //   · 颜色只有 5 个（cb0[6..10]：粉/蓝/黄绿/黄/橙），按 aY 的 0.2/0.4/0.6/0.8 阈值挑；
    //     cb0[5] 那个 0.5 的灰在色板里永远取不到（第一步 lerp 的条件恒为 0）——
    //     也就是说「灰扇」不是色板里的灰，而是下面那个 k。
    //   · 灰扇 = k = |径向-0.25|×3+0.25 的**灰度斜坡**，而且 alpha 还要 ×0.5。
    //     它不是我之前画的那种 214 的白条。
    //   · 网点是屏幕空间的 frac((屏幕坐标×2-1)×64)，被 (dots×band-0.1)×24+0.1 夹到 [0,1]，
    //     再取 1- 得到 mask；band 来自 fract(径向×4-2(1-alpha)) 的三段折线，
    //     所以网点只出现在一圈圈的环带上，而不是铺满。
    //   · 收尾：radial<=0.5 收圆、radial 从 0.4149 起径向衰减、整体 ×0.75。
    //
    // SkiaSharp 2.88.9 的 SKRuntimeEffect 一画就原生崩溃（连 hello-world 级 shader 都崩），
    // 所以这里在 CPU 上逐像素算完写进位图，再交给 Skia 缩放绘制。
    // tools/hanabi_ref.py 是同一套公式的 numpy 版本，用来跟官方渲染图逐角度对数。

    private static readonly SKColor[] RainbowSectors =   // cb0[6..10]，按 aY 从大到小
    {
        new(237, 58, 156),   // 0.8~1.0 粉   cb0[6]
        new(0, 160, 233),    // 0.6~0.8 蓝   cb0[7]
        new(184, 210, 0),    // 0.4~0.6 黄绿 cb0[8]
        new(255, 216, 0),    // 0.2~0.4 黄   cb0[9]
        new(255, 90, 18)     // 0.0~0.2 橙   cb0[10]
    };

    /// <summary>位图边长上限。彩虹层是纯几何图形，铺满到屏幕时用不着 1:1。</summary>
    private const int RainbowBitmapCap = 1024;

    private static SKBitmap? _rainbowBitmap;

    private static float Frac(float v) => v - MathF.Floor(v);

    private void DrawRainbow(SKCanvas canvas, float cx, float cy, float quad, in HanabiSample sample)
    {
        if (quad < 4) return;

        var tintA = (float)Math.Clamp(sample.A, 0, 1);
        if (tintA <= 0.002) return;

        var n = (int)Math.Clamp(MathF.Round(quad), 16, RainbowBitmapCap);
        if (_rainbowBitmap is not { } bitmap || bitmap.Width != n)
        {
            _rainbowBitmap?.Dispose();
            bitmap = new SKBitmap(new SKImageInfo(n, n, SKColorType.Rgba8888, SKAlphaType.Premul));
            _rainbowBitmap = bitmap;
        }

        var half = quad * 0.5f;
        var rot = (float)sample.Rotation;
        var cos = MathF.Cos(rot);
        var sin = MathF.Sin(rot);
        var sw = (float)Math.Max(1, _size.Width);
        var sh = (float)Math.Max(1, _size.Height);

        // cb0[4].x * cb1[0].y = 9 * frame.z，再乘 1/5 当色板步进
        var paletteOffset = (float)(Math.Floor(9.0 * (_now % 1000.0)) * 0.2 % 1.0);

        // tint.a 参与两个地方：环带相位（2*(1-a)）和淡出
        var ringOffset = 2f * (1f - tintA);
        var gate = Math.Clamp((1f - tintA) / 0.2f, 0f, 1f);
        var fadeBase = Math.Clamp(tintA / 0.4f, 0f, 1f);

        const float nearEdge = 0.66f * 0.2499f + 0.25f;   // cb0[11].z

        unsafe
        {
            var pixels = (uint*)bitmap.GetPixels().ToPointer();

            for (var by = 0; by < n; by++)
            {
                var v = (by + 0.5f) / n;          // uv.y
                var a0 = v * 2f - 1f;             // r0.x = uv.y*2-1
                var dyq = (v - 0.5f) * quad;
                var row = pixels + by * n;

                for (var bx = 0; bx < n; bx++)
                {
                    var u = (bx + 0.5f) / n;      // uv.x
                    var b0 = u * 2f - 1f;         // r0.y = uv.x*2-1

                    // 圆形遮罩之外直接跳过（四角占 21% 的像素，且 atan2 不便宜）
                    var rx = u - 0.5f;
                    var ry = v - 0.5f;
                    var radial = MathF.Sqrt(rx * rx + ry * ry);
                    if (radial > 0.5f)
                    {
                        row[bx] = 0;
                        continue;
                    }

                    // ---- atan2 近似（照抄）----
                    var absA = MathF.Abs(a0);
                    var absB = MathF.Abs(b0);
                    var ratio = MathF.Min(absA, absB) / MathF.Max(MathF.Max(absA, absB), 1e-12f);
                    var sq = ratio * ratio;
                    var poly = sq * 0.0208351f - 0.085133f;
                    poly = sq * poly + 0.180141f;
                    poly = sq * poly - 0.3302995f;
                    var coef = sq * poly + 0.999866f;
                    var angle = ratio * coef;
                    var tilt = -(angle * 2f) + 1.5707964f;
                    if (absB >= absA) tilt = 0f;          // 仅当 |r0.y| < |r0.x| 时保留
                    angle = angle + tilt;
                    if (b0 < 0f) angle -= 3.1415927f;     // (1 - step(-y, y)) * -pi

                    // ---- 象限修正 ----
                    var lo = MathF.Min(b0, a0);
                    var hi = MathF.Max(b0, a0);
                    if (hi >= 0f && lo < 0f) angle = -angle;
                    angle = (angle + 3.1415927f) * 3f;

                    var aY = Frac(angle * 0.159154937f);
                    var aZ = Frac(angle * 0.795774698f);
                    var aX = Frac(angle * 0.795774698f + 0.03f);

                    // ---- 色板 ----
                    var y = Frac(paletteOffset + aY);
                    var color = y >= 0.8f ? RainbowSectors[0]
                        : y >= 0.6f ? RainbowSectors[1]
                        : y >= 0.4f ? RainbowSectors[2]
                        : y >= 0.2f ? RainbowSectors[3]
                        : RainbowSectors[4];

                    var isSector = 0.56f >= aX;    // 这一半不打网点
                    var isGray = 0.5f >= aZ;       // 这一半是灰度斜坡

                    // ---- 环带 + 网点 ----
                    var rc = Frac(radial * 4f - ringOffset);
                    var band = rc <= 0.8f ? rc * rc * 1.125f + 0.1f
                        : rc <= 0.82f ? (rc - 0.8f) * -25f + 1f
                        : (rc - 0.82f) * -2.7777777f + 0.5f;

                    // mask = 1 - clamp((dots*band - 0.1)*24 + 0.1)，band 太小时这个式子恒为 0
                    // （dots<=1 时 dots*band-0.1 < 0 就够），那种像素不用算网点
                    var mask = 1f;
                    if (!isSector && band > 0.1375f)
                    {
                        var dqx = (u - 0.5f) * quad;
                        var sxp = cx + cos * dqx - sin * dyq;
                        var syp = cy + sin * dqx + cos * dyq;
                        var gx = Frac((sxp / sw * 2f - 1f) * 64f) - 0.5f;
                        var gy = Frac(((1f - syp / sh) * 2f - 1f) * 64f) - 0.5f;
                        var dot = 1f - MathF.Sqrt(gx * gx + gy * gy);
                        var dots = dot * dot;
                        mask = 1f - Math.Clamp((dots * band - 0.1f) * 24f + 0.1f, 0f, 1f);
                    }

                    var sector = isSector ? 1f : mask;

                    // k 是灰度斜坡：中心亮、径向 0.25 处最暗、到边缘又亮
                    var k = MathF.Abs(radial - 0.25f) * 3f + 0.25f;

                    float r, g, b;
                    if (isGray)
                    {
                        r = g = b = k;
                    }
                    else
                    {
                        var m = (1f + sector * (k - 1f)) / 255f;
                        r = color.Red * m;
                        g = color.Green * m;
                        b = color.Blue * m;
                    }

                    // ---- alpha ----
                    var a1 = Math.Clamp((radial - gate) * -3f - 0.5f, 0f, 1f);
                    var a2 = Math.Clamp((fadeBase + radial) * 2f - 1f, 0f, 1f);
                    var alpha = a1 * a2 * (isGray ? 0.5f : 1f);
                    alpha *= Math.Clamp((nearEdge - radial) / (0.5f - nearEdge) + 1f, 0f, 1f);
                    alpha *= 0.75f;
                    if (alpha <= 0.002f)
                    {
                        row[bx] = 0;
                        continue;
                    }

                    var aa = (uint)Math.Clamp((int)(alpha * 255f + 0.5f), 0, 255);
                    var rr = (uint)Math.Clamp((int)(r * alpha * 255f + 0.5f), 0, (int)aa);
                    var gg = (uint)Math.Clamp((int)(g * alpha * 255f + 0.5f), 0, (int)aa);
                    var bb = (uint)Math.Clamp((int)(b * alpha * 255f + 0.5f), 0, (int)aa);
                    row[bx] = (aa << 24) | (bb << 16) | (gg << 8) | rr;
                }
            }
        }

        canvas.Save();
        canvas.RotateDegrees((float)(rot * 180 / Math.PI), cx, cy);
        using (var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Low })
        {
            canvas.DrawBitmap(bitmap, new SKRect(cx - half, cy - half, cx + half, cy + half), paint);
        }

        canvas.Restore();
    }

    /// <summary>rgb = 贴图 × tint × 2^exposure，对应 SpritePixel 里 rgb 那一行。</summary>
    private static SKColorFilter TintScaled(SKColor color, float gain)
    {
        if (MathF.Abs(gain - 1f) < 0.001f) return Tint(color);

        var key = ((uint)color, gain);
        if (ScaledFilters.TryGetValue(key, out var cached)) return cached;

        var scale = SKColorFilter.CreateColorMatrix(new[]
        {
            gain, 0, 0, 0, 0,
            0, gain, 0, 0, 0,
            0, 0, gain, 0, 0,
            0, 0, 0, 1, 0
        });
        // CreateCompose 会持有这两个滤镜，不能提前 Dispose
        var composed = SKColorFilter.CreateCompose(scale, Tint(color));
        ScaledFilters[key] = composed;
        return composed;
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
        foreach (var filter in ScaledFilters.Values) filter.Dispose();
        ScaledFilters.Clear();
    }
}
