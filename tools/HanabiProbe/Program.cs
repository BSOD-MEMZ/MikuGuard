using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MikuGuard.Services.Hanabi;

namespace MikuGuard.Tools.HanabiProbe;

/// <summary>
/// 花火离线探针：不开宿主、不等摄像头，直接把某一时刻的画面渲成 PNG。
/// 用法：dotnet run --project tools/HanabiProbe -- [输出目录]
/// </summary>
internal static class Program
{
    private const int Width = 1200;
    private const int Height = 675;

    [STAThread]
    private static int Main(string[] args)
    {
        AppBuilder.Configure<ProbeApp>().UsePlatformDetect().SetupWithoutStarting();

        var outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "frames");
        var only = args.FirstOrDefault(a => a.StartsWith("--only"))?.Split('=').LastOrDefault();
        Directory.CreateDirectory(outDir);

        var probe = new HanabiSimulation(20261108);
        if (!probe.Load())
        {
            Console.WriteLine("参数加载失败");
            return 1;
        }

        Console.WriteLine($"发射器 {probe.Emitters.Count} 个");

        // 同一颗种子、同一时刻，保证每次跑出来一样
        var times = new[] { 0.12, 0.20, 0.35, 0.60, 1.00 };
        foreach (var t in times)
        {
            var sim = new HanabiSimulation(20261108);
            sim.Load();
            if (only != null)
            {
                // 只保留指定材质，方便单独排查某一层（先过滤再 Burst，粒子索引才对得上）
                foreach (var em in sim.Emitters.Where(e => !e.Material.ToString().Equals(only)).ToList())
                    sim.Emitters.Remove(em);
            }

            sim.Burst(0, 0.5, 0.5);

            var total = sim.Bursts.Sum(b => b.Particles.Count);

            if (Math.Abs(t - 0.999) < 0.001)
            {
                var scale0 = Math.Max(Width, Height) / 1000.0;
                foreach (var b in sim.Bursts)
                foreach (var pt in b.Particles)
                {
                    var em = sim.Emitters[pt.Emitter];
                    if (em.Material is not (80 or 49 or 50)) continue;
                    var ok = HanabiSimulation.Sample(em, pt, t, b.Start, scale0, out var sp);
                    Console.WriteLine($"  mat{em.Material} {em.Name} ok={ok} dia={sp.Diameter:0} a={sp.A:0.00} rot={sp.Rotation:0.00} tex={string.Join("/", em.Textures)}");
                }
            }
            using var bitmap = new RenderTargetBitmap(new PixelSize(Width, Height), new Vector(96, 96));
            using (var ctx = bitmap.CreateDrawingContext())
            {
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(10, 12, 16)),
                    new Rect(0, 0, Width, Height));
                HanabiRenderer.Render(ctx, sim, t, new Size(Width, Height));
            }

            var path = Path.Combine(outDir, $"frame_{t:0.00}.png");
            bitmap.Save(path);
            Console.WriteLine($"t={t:0.00}s 粒子 {total} → {path}");
        }

        return 0;
    }
}

/// <summary>Avalonia 应用壳，只为让 RenderTargetBitmap 有平台渲染后端。</summary>
internal sealed class ProbeApp : Application
{
}
