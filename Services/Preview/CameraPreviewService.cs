using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media;
using Windows.Storage.Streams;

namespace MikuGuard.Services.Preview;

/// <summary>
/// 摄像头预览：用 WinRT MediaCapture + MediaFrameReader 抓帧，转成 BGRA32 交给界面。
/// 只在预览窗口打开期间占用设备。
/// </summary>
public sealed class CameraPreviewService : IDisposable
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private SoftwareBitmap? _converted;
    private IBuffer? _buffer;
    private byte[]? _pixels;

    /// <summary>拿到一帧（BGRA32，自上而下）。</summary>
    public event Action<byte[], int, int>? FrameReady;

    public string? LastError { get; private set; }

    public bool IsRunning => _reader != null;

    /// <summary>
    /// 打开指定摄像头的预览。deviceId 可以是设备接口路径，留空则用第一台摄像头。
    /// </summary>
    public async Task<bool> StartAsync(string? deviceId, string? deviceName)
    {
        await StopAsync();

        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            var target = FindDevice(devices, deviceId, deviceName);
            if (target == null)
            {
                LastError = "没有找到可用的摄像头";
                return false;
            }

            var capture = new MediaCapture();
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = target.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl
            });

            var source = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                         ?? capture.FrameSources.Values.FirstOrDefault();
            if (source == null)
            {
                capture.Dispose();
                LastError = "这台摄像头没有可用的画面输出";
                return false;
            }

            await TryUseSmallPreviewFormatAsync(capture);

            var reader = await capture.CreateFrameReaderAsync(source);
            reader.FrameArrived += OnFrameArrived;

            var status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                reader.FrameArrived -= OnFrameArrived;
                reader.Dispose();
                capture.Dispose();
                LastError = $"画面启动失败：{status}";
                return false;
            }

            _capture = capture;
            _reader = reader;
            LastError = null;
            GuardLog.Info($"[preview] 预览已启动：{target.Name}");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.HResult == unchecked((int)0xC00D3704)
                ? "摄像头正被其它程序占用"
                : $"打开摄像头失败：0x{ex.HResult:X8} {ex.Message}";
            GuardLog.Warn($"[preview] {LastError}");
            await StopAsync();
            return false;
        }
    }

    /// <summary>
    /// 预览只要看清楚画面就行，把分辨率压到 640x480 附近，
    /// 免得每帧都按 720p/1080p 做 NV12→BGRA 转换把 CPU 吃满。
    /// </summary>
    private static async Task TryUseSmallPreviewFormatAsync(MediaCapture capture)
    {
        try
        {
            var properties = capture.VideoDeviceController
                .GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview)
                .OfType<Windows.Media.MediaProperties.VideoEncodingProperties>()
                .Where(p => p.Width > 0 && p.Height > 0)
                .OrderBy(p => Math.Abs(p.Width - 640) + Math.Abs(p.Height - 480))
                .ToList();

            if (properties.Count == 0) return;

            await capture.VideoDeviceController.SetMediaStreamPropertiesAsync(
                MediaStreamType.VideoPreview, properties[0]);
        }
        catch (Exception ex)
        {
            GuardLog.Warn($"[preview] 设置预览分辨率失败（不影响使用）：{ex.Message}");
        }
    }

    private static DeviceInformation? FindDevice(DeviceInformationCollection devices, string? deviceId, string? deviceName)
    {
        if (devices.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var want = NormalizeId(deviceId);
            var byId = devices.FirstOrDefault(d => NormalizeId(d.Id) == want);
            if (byId != null) return byId;
        }

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var byName = devices.FirstOrDefault(d => d.Name == deviceName);
            if (byName != null) return byName;
        }

        return devices[0];
    }

    /// <summary>设备 id 归一化：小写 + 去掉 DeviceInformation 的 \GLOBAL 尾巴。</summary>
    private static string NormalizeId(string id)
    {
        var s = id.Trim();
        const string globalSuffix = @"\GLOBAL";
        if (s.EndsWith(globalSuffix, StringComparison.OrdinalIgnoreCase)) s = s[..^globalSuffix.Length];
        return s.ToLowerInvariant();
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap == null) return;

            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            var size = width * height * 4;

            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            {
                _converted?.Dispose();
                _converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                bitmap = _converted;
            }

            if (_buffer == null || _buffer.Capacity < size)
                _buffer = new global::Windows.Storage.Streams.Buffer((uint)size);
            if (_pixels == null || _pixels.Length != size)
                _pixels = new byte[size];

            bitmap!.CopyToBuffer(_buffer);
            using var dataReader = DataReader.FromBuffer(_buffer);
            dataReader.ReadBytes(_pixels);

            FrameReady?.Invoke(_pixels, width, height);
        }
        catch (Exception ex)
        {
            GuardLog.Warn($"[preview] 取帧失败：{ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        var reader = _reader;
        _reader = null;
        if (reader != null)
        {
            try
            {
                reader.FrameArrived -= OnFrameArrived;
                await reader.StopAsync();
            }
            catch
            {
                // 忽略
            }

            reader.Dispose();
        }

        _capture?.Dispose();
        _capture = null;
        _converted?.Dispose();
        _converted = null;
        _buffer = null;
        _pixels = null;
    }

    public void Dispose()
    {
        _ = StopAsync();
    }
}
