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
        Directory.CreateDirectory(outDir);

        var probe = new HanabiSimulation(20261108);
        if (!probe.Load())
        {
            Console.WriteLine("参数加载失败");
            return 1;
        }

        Console.WriteLine($"发射器 {probe.Emitters.Count} 个");

        // 同一颗种子、同一时刻，保证每次跑出来一样
        var times = new[] { 0.10, 0.30, 0.55, 0.90, 1.40 };
        foreach (var t in times)
        {
            var sim = new HanabiSimulation(20261108);
            sim.Load();
            sim.Burst(0, 0.5, 0.5);

            var total = sim.Bursts.Sum(b => b.Particles.Count);
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
