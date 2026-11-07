using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Security.Cryptography;

var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
Console.WriteLine($"找到 {devices.Count} 个视频设备");
foreach (var d in devices)
    Console.WriteLine($"  {d.Name} | {d.Id}");

var target = devices.FirstOrDefault();
if (target == null)
{
    Console.WriteLine("没有可用摄像头");
    return;
}

var capture = new MediaCapture();
var settings = new MediaCaptureInitializationSettings
{
    VideoDeviceId = target.Id,
    StreamingCaptureMode = StreamingCaptureMode.Video,
    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
    SharingMode = MediaCaptureSharingMode.ExclusiveControl
};

try
{
    await capture.InitializeAsync(settings);
    Console.WriteLine("初始化成功");
}
catch (Exception ex)
{
    Console.WriteLine($"初始化失败 0x{ex.HResult:X8} {ex.Message}");
    return;
}

var source = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
             ?? capture.FrameSources.Values.FirstOrDefault();
if (source == null)
{
    Console.WriteLine("没有帧源");
    return;
}

Console.WriteLine($"帧源格式：{string.Join(", ", source.SupportedFormats.Select(f => f.Subtype))}");

var reader = await capture.CreateFrameReaderAsync(source);
var done = new TaskCompletionSource<string>();

reader.FrameArrived += (s, e) =>
{
    using var frame = s.TryAcquireLatestFrame();
    if (frame == null) return;

    var bitmap = frame.VideoMediaFrame?.SoftwareBitmap;
    if (bitmap == null)
    {
        done.TrySetResult("SoftwareBitmap 为 null（CPU 路径不可用）");
        return;
    }

    var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
    bitmap.CopyToBuffer(buffer);
    CryptographicBuffer.CopyToByteArray(buffer, out var bytes);
    done.TrySetResult($"拿到一帧：{bitmap.PixelWidth}x{bitmap.PixelHeight} {bitmap.BitmapPixelFormat} 共 {bytes.Length} 字节");
};

var status = await reader.StartAsync();
Console.WriteLine($"Reader 启动：{status}");

var finished = await Task.WhenAny(done.Task, Task.Delay(6000));
Console.WriteLine(finished == done.Task ? await done.Task : "6 秒内没有拿到帧");

await reader.StopAsync();
reader.Dispose();
capture.Dispose();
