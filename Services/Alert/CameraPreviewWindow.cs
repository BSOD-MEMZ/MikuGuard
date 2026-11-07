using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MikuGuard.Services.Preview;

namespace MikuGuard.Services.Alert;

/// <summary>摄像头预览窗口：把 MediaCapture 抓到的帧画到 Image 里。</summary>
public sealed class CameraPreviewWindow : Window
{
    private readonly CameraPreviewService _service = new();
    private readonly Image _image;
    private readonly TextBlock _status;
    private readonly Button _retryButton;
    private readonly string _deviceId;
    private readonly string _deviceName;

    private WriteableBitmap? _bitmap;
    private int _frameWidth;
    private int _frameHeight;
    private bool _updateQueued;
    private bool _closed;

    public CameraPreviewWindow(string deviceId, string deviceName)
    {
        _deviceId = deviceId;
        _deviceName = deviceName;

        Title = string.IsNullOrWhiteSpace(deviceName) ? "摄像头预览" : $"摄像头预览 — {deviceName}";
        Width = 820;
        Height = 560;
        MinWidth = 420;
        MinHeight = 300;
        Background = new SolidColorBrush(Color.Parse("#11151C"));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _status = new TextBlock
        {
            Text = "正在打开摄像头…",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#B9C4D4")),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        _retryButton = new Button
        {
            Content = "重新连接",
            FontSize = 12,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(12, 0, 0, 0),
            IsVisible = false
        };
        _retryButton.Click += async (_, _) => await StartAsync();

        var bottomBar = new DockPanel
        {
            Margin = new Thickness(12, 8, 12, 10),
            LastChildFill = true
        };
        DockPanel.SetDock(_retryButton, Dock.Right);
        bottomBar.Children.Add(_retryButton);
        bottomBar.Children.Add(_status);

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("* , Auto")
        };
        Grid.SetRow(_image, 0);
        Grid.SetRow(bottomBar, 1);
        grid.Children.Add(_image);
        grid.Children.Add(bottomBar);

        Content = grid;

        _service.FrameReady += OnFrameReady;
        Closed += async (_, _) =>
        {
            _closed = true;
            _service.FrameReady -= OnFrameReady;
            await _service.StopAsync();
            _service.Dispose();
        };
    }

    public async Task StartAsync()
    {
        _retryButton.IsVisible = false;
        _status.Text = "正在打开摄像头…";

        var ok = await _service.StartAsync(_deviceId, _deviceName);
        if (_closed) return;

        if (!ok)
        {
            _status.Text = _service.LastError ?? "无法打开摄像头";
            _retryButton.IsVisible = true;
        }
        else
        {
            _status.Text = "已连接";
        }
    }

    private void OnFrameReady(byte[] pixels, int width, int height)
    {
        if (_closed) return;

        // 界面跟不上就丢帧，别把消息队列堆爆
        if (_updateQueued) return;
        _updateQueued = true;

        Dispatcher.UIThread.Post(() =>
        {
            _updateQueued = false;
            if (_closed) return;

            try
            {
                if (_bitmap == null || _frameWidth != width || _frameHeight != height)
                {
                    _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                        PixelFormat.Bgra8888, AlphaFormat.Premul);
                    _frameWidth = width;
                    _frameHeight = height;
                    _image.Source = _bitmap;
                }

                using var fb = _bitmap.Lock();
                var rowBytes = width * 4;
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(pixels, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
                }

                if (!string.IsNullOrEmpty(_status.Text) && _status.Text != "已连接")
                {
                    _status.Text = "已连接";
                    _retryButton.IsVisible = false;
                }
            }
            catch (Exception ex)
            {
                _status.Text = $"画面渲染失败：{ex.Message}";
            }
        });
    }
}
