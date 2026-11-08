using System.Text.Json;

namespace MikuGuard.Services.Hanabi;

/// <summary>
/// maimai 全屏烟花的参数与粒子模拟。
/// 参数表与曲线求值直接沿用 thock-maimai（MIT）里那份 maimai-fireworks.json，
/// 只把 CPU 侧的粒子推进逻辑照搬过来；渲染换成了 Avalonia。
/// 素材版权见 Assets/Hanabi/ASSET-LICENSE.txt。
/// </summary>
public sealed class HanabiCurve
{
    public int Mode { get; set; }

    public double Min { get; set; }

    public double Max { get; set; }

    public double[][] Hi { get; set; } = Array.Empty<double[]>();

    public double[][] Lo { get; set; } = Array.Empty<double[]>();

    /// <summary>按 t 取样（对应原实现里的 curve()）。</summary>
    public double Evaluate(double t, double r)
    {
        switch (Mode)
        {
            case 1:
                return Hermite(Hi, t) * Max;
            case 2:
                return Lerp(Hermite(Lo, t) * Min, Hermite(Hi, t) * Max, r);
            case 3:
                return Lerp(Min, Max, r);
            default:
                return Max;
        }
    }

    /// <summary>三次 Hermite 样条，键是 [时间, 值, 入切线, 出切线]。</summary>
    public static double Hermite(double[][] keys, double t)
    {
        if (keys.Length == 0) return 1;
        if (t <= keys[0][0]) return keys[0][1];

        for (var i = 1; i < keys.Length; i++)
        {
            if (t > keys[i][0]) continue;

            var a = keys[i - 1][0];
            var b = keys[i][0];
            var dt = b - a;
            if (dt <= 0) dt = 1e-6;

            var u = (t - a) / dt;
            return (2 * u * u * u - 3 * u * u + 1) * keys[i - 1][1]
                   + (u * u * u - 2 * u * u + u) * dt * keys[i - 1][3]
                   + (-2 * u * u * u + 3 * u * u) * keys[i][1]
                   + (u * u * u - u * u) * dt * keys[i][2];
        }

        return keys[^1][1];
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}

/// <summary>颜色渐变（rgb 关键帧 + alpha 关键帧）。</summary>
public sealed class HanabiGradient
{
    public double[][] Rgb { get; set; } = Array.Empty<double[]>();

    public double[][] Alpha { get; set; } = Array.Empty<double[]>();

    /// <summary>取 t 处的 RGBA（各分量 0~1）。</summary>
    public double[] Sample(double t) => new[]
    {
        KeyValue(Rgb, t, 1), KeyValue(Rgb, t, 2), KeyValue(Rgb, t, 3), KeyValue(Alpha, t, 1)
    };

    private static double KeyValue(double[][] keys, double t, int component)
    {
        if (keys.Length == 0) return 1;
        if (t <= keys[0][0]) return keys[0][component];

        for (var i = 1; i < keys.Length; i++)
        {
            if (t > keys[i][0]) continue;

            var dt = keys[i][0] - keys[i - 1][0];
            if (dt <= 0) dt = 1e-6;
            var u = (t - keys[i - 1][0]) / dt;
            return keys[i - 1][component] + (keys[i][component] - keys[i - 1][component]) * u;
        }

        return keys[^1][component];
    }
}

/// <summary>一个发射器（一层烟花）。</summary>
public sealed class HanabiEmitter
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>49 = 彩虹，50 = 圆环，48/80 = 星光/放射。</summary>
    public int Material { get; set; }

    public string[] Textures { get; set; } = Array.Empty<string>();

    public double Delay { get; set; }

    public double Duration { get; set; }

    public double Rate { get; set; }

    public double Radius { get; set; }

    public double RadiusThickness { get; set; }

    public double Exposure { get; set; }

    public bool ColorRandom { get; set; }

    public HanabiCurve Life { get; set; } = new();

    public HanabiCurve Size { get; set; } = new();

    public HanabiCurve Speed { get; set; } = new();

    public HanabiCurve Rotation { get; set; } = new();

    public HanabiCurve SizeCurve { get; set; } = new();

    public HanabiCurve? VelocityLimit { get; set; }

    public HanabiCurve? AngularVelocity { get; set; }

    public HanabiGradient ColorHi { get; set; } = new();

    public HanabiGradient ColorLo { get; set; } = new();

    public double[][] Bursts { get; set; } = Array.Empty<double[]>();

    /// <summary>绘制顺序：彩虹 → 圆环 → 星光/放射（和原实现分三层画一致）。</summary>
    public int Layer => Material == 49 ? 0 : Material == 50 ? 1 : 2;
}

/// <summary>一个粒子。</summary>
public sealed class HanabiParticle
{
    public int Emitter;
    public double Birth;
    public double Life;
    public double Diameter;
    public double Angle;
    public double Rotation;
    public double Radius;
    public double Speed;
    public double Spin;
    public double Random;
}

/// <summary>一次「开花」。</summary>
public sealed class HanabiBurst
{
    public double Start;
    public double OriginX = 0.5;
    public double OriginY = 0.5;
    public List<HanabiParticle> Particles { get; } = new();
}

/// <summary>烟花模拟：读参数 → 生成粒子 → 逐帧推进。</summary>
public sealed class HanabiSimulation
{
    /// <summary>一次开花最长活这么久（秒），和原实现一致。</summary>
    public const double BurstLifetime = 1.75;

    private readonly Random _random;

    /// <summary>传 seed 可以复现同一朵花（离线探针用）。</summary>
    public HanabiSimulation(int? seed = null) => _random = seed.HasValue ? new Random(seed.Value) : new Random();

    public List<HanabiEmitter> Emitters { get; } = new();

    public List<HanabiBurst> Bursts { get; } = new();

    public bool Ready => Emitters.Count > 0;

    /// <summary>从插件目录读 maimai-fireworks.json。</summary>
    public bool Load()
    {
        try
        {
            var path = MikuGuardPaths.Resolve(@"Assets\Hanabi\maimai-fireworks.json");
            if (!File.Exists(path))
            {
                GuardLog.Warn($"[hanabi] 找不到参数文件：{path}");
                return false;
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var emitters = JsonSerializer.Deserialize<List<HanabiEmitter>>(File.ReadAllText(path), options);
            if (emitters == null || emitters.Count == 0) return false;

            Emitters.Clear();
            Emitters.AddRange(emitters);
            GuardLog.Info($"[hanabi] 已加载 {Emitters.Count} 个发射器");
            return true;
        }
        catch (Exception ex)
        {
            GuardLog.Error("[hanabi] 参数加载失败", ex);
            return false;
        }
    }

    /// <summary>在归一化坐标 (x, y) 处开一次花。</summary>
    public void Burst(double now, double x, double y)
    {
        Expire(now);
        if (Bursts.Count >= 6) Bursts.RemoveAt(0);

        var burst = new HanabiBurst { Start = now, OriginX = x, OriginY = y };

        for (var i = 0; i < Emitters.Count; i++)
        {
            var emitter = Emitters[i];

            var births = new List<double>();
            foreach (var b in emitter.Bursts)
            {
                var count = (int)Math.Round(b[1]);
                for (var j = 0; j < count; j++) births.Add(b[0]);
            }

            var total = (int)Math.Round(emitter.Rate * emitter.Duration);
            for (var j = 0; j < total; j++)
                births.Add(emitter.Rate > 0 ? j / emitter.Rate : 0);

            foreach (var birth in births)
            {
                var rnd = _random.NextDouble();
                var particle = new HanabiParticle
                {
                    Emitter = i,
                    Random = rnd,
                    Birth = birth + emitter.Delay,
                    Life = Math.Max(0.001, emitter.Life.Evaluate(0, rnd)),
                    Diameter = emitter.Size.Evaluate(0, _random.NextDouble()) * 100,
                    Angle = _random.NextDouble() * Math.PI * 2,
                    Rotation = emitter.Rotation.Evaluate(0, _random.NextDouble()),
                    Radius = emitter.Radius * (1 - emitter.RadiusThickness * _random.NextDouble()) * 100,
                    Speed = emitter.Speed.Evaluate(0, rnd) * 100,
                    Spin = emitter.AngularVelocity?.Evaluate(0, _random.NextDouble()) ?? 0
                };

                burst.Particles.Add(particle);
            }
        }

        Bursts.Add(burst);
    }

    /// <summary>清掉过期的花。</summary>
    public void Expire(double now) =>
        Bursts.RemoveAll(b => now - b.Start >= BurstLifetime);

    /// <summary>算粒子在当前时刻的位置、尺寸、角度和颜色。</summary>
    public static bool Sample(in HanabiEmitter emitter, in HanabiParticle particle, double now, double burstStart,
        double screenScale, out HanabiSample sample)
    {
        sample = default;

        var age = now - burstStart - particle.Birth;
        if (age < 0 || age > particle.Life) return false;

        var t = age / particle.Life;

        // 速度上限是个随时间变化的曲线，按固定 120Hz 积分，结果与帧率无关
        var distance = particle.Radius;
        var steps = Math.Max(1, (int)Math.Ceiling(age * 120));
        var dt = age / steps;
        for (var j = 0; j < steps; j++)
        {
            var limit = emitter.VelocityLimit == null
                ? particle.Speed
                : emitter.VelocityLimit.Evaluate((j + 0.5) * dt / particle.Life, particle.Random) * 100;
            distance += Math.Min(particle.Speed, Math.Max(0, limit)) * dt;
        }

        var color = emitter.ColorHi.Sample(t);
        if (emitter.ColorRandom)
        {
            var low = emitter.ColorLo.Sample(t);
            for (var c = 0; c < 4; c++) color[c] = low[c] + (color[c] - low[c]) * particle.Random;
        }

        // exposure 是 2 的指数（shader 里 exp2(frame.w)）。
        // 原实现把它乘在 rgb 上、再乘以 tex.a，最终可见亮度 = rgb × 曝光 × 透明度；
        // 这里没有 HDR，就把曝光折进 alpha（颜色保持色相，不然乘完就全白成一片）。
        var gain = Math.Pow(2, emitter.Exposure);

        sample = new HanabiSample
        {
            Distance = distance,
            Diameter = particle.Diameter * Math.Max(0, emitter.SizeCurve.Evaluate(t, particle.Random)) * screenScale,
            Rotation = -particle.Rotation - particle.Spin * age,
            R = Math.Clamp(color[0], 0, 1),
            G = Math.Clamp(color[1], 0, 1),
            B = Math.Clamp(color[2], 0, 1),
            A = Math.Clamp(color[3] * gain, 0, 1)
        };

        return true;
    }
}

/// <summary>某一帧里一个粒子的求值结果。</summary>
public struct HanabiSample
{
    public double Distance;
    public double Diameter;
    public double Rotation;
    public double R;
    public double G;
    public double B;
    public double A;
}
