using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using MikuGuard.Services.Toast;

namespace MikuGuard.Services.Alert;

/// <summary>
/// Windows 通知不可用时的回退提示窗：置顶小卡片，按钮行为和 Toast 一致。
/// 完全用代码构建，避免 XAML 依赖。
/// </summary>
public sealed class GuardAlertWindow : Window
{
    private const double ImageHeight = 128;
    private const double BaseHeight = 186;

    private static readonly Dictionary<string, Bitmap> ImageCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly TextBlock _titleText;
    private readonly TextBlock _bodyText;
    private readonly StackPanel _buttonRow;
    private readonly Ellipse _dot;
    private readonly Border _imageHost;
    private readonly Image _image;

    /// <summary>点击按钮时回调，参数与 Toast 的 arguments 一致。</summary>
    public event Action<string>? ActionInvoked;

    public GuardAlertWindow()
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Background = Brushes.Transparent;
        Width = 388;
        Height = BaseHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "MikuGuard";

        _dot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = new ImmutableSolidColorBrush(Color.Parse("#FF43A047")),
            VerticalAlignment = VerticalAlignment.Center
        };

        _titleText = new TextBlock
        {
            Text = "摄像头正在被使用",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = new ImmutableSolidColorBrush(Color.Parse("#FFF2F7FF")),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        _bodyText = new TextBlock
        {
            Text = "",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new ImmutableSolidColorBrush(Color.Parse("#CCE6EDF7"))
        };

        _buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        _image = new Image
        {
            Stretch = Stretch.UniformToFill,
            Height = ImageHeight
        };

        _imageHost = new Border
        {
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Height = ImageHeight,
            IsVisible = false,
            Margin = new Thickness(0, 0, 0, 14),
            Child = _image
        };

        var titleHost = new Border { Child = _titleText, Margin = new Thickness(8, 0, 0, 0) };
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _dot, titleHost }
        };

        var root = new StackPanel
        {
            Children = { _imageHost, header, _bodyText, _buttonRow }
        };
        _bodyText.Margin = new Thickness(0, 10, 0, 0);
        _buttonRow.Margin = new Thickness(0, 14, 0, 0);

        Content = new Border
        {
            Background = new ImmutableSolidColorBrush(Color.Parse("#F2141926")),
            BorderBrush = new ImmutableSolidColorBrush(Color.Parse("#5543A047")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 14, 16, 14),
            Child = root
        };
    }

    /// <summary>显示告警；已显示时只更新内容。</summary>
    public void Present(string title, string body, IReadOnlyList<ToastActionButton> actions, string? imagePath)
    {
        _titleText.Text = title;
        _bodyText.Text = body;

        _buttonRow.Children.Clear();
        foreach (var action in actions)
            _buttonRow.Children.Add(CreateButton(action.Content, action.Arguments));

        var bitmap = LoadImage(imagePath);
        _image.Source = bitmap;
        _imageHost.IsVisible = bitmap != null;
        Height = bitmap != null ? BaseHeight + ImageHeight + 14 : BaseHeight;

        if (!IsVisible) Show();

        BringToTop();
        PlaceWindow();
    }

    private static Bitmap? LoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            if (ImageCache.TryGetValue(path, out var cached)) return cached;
            var bitmap = new Bitmap(path);
            ImageCache[path] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private Button CreateButton(string content, string arguments)
    {
        var isDanger = arguments.Contains("disablecamera", StringComparison.Ordinal) ||
                       arguments.Contains("killapp", StringComparison.Ordinal);

        var button = new Button
        {
            Content = content,
            FontSize = 12.5,
            Padding = new Thickness(14, 6, 14, 6),
            Background = new ImmutableSolidColorBrush(Color.Parse(isDanger ? "#33EF5350" : "#22FFFFFF")),
            Foreground = new ImmutableSolidColorBrush(Color.Parse(isDanger ? "#FFFFCDD2" : "#FFE6EDF7")),
            BorderBrush = new ImmutableSolidColorBrush(Color.Parse(isDanger ? "#66EF5350" : "#33FFFFFF")),
            BorderThickness = new Thickness(1)
        };

        button.Click += (_, _) =>
        {
            var args = arguments;
            Hide();
            ThreadPool.QueueUserWorkItem(_ => ActionInvoked?.Invoke(args));
        };

        return button;
    }

    private void PlaceWindow()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) return;

        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var area = screen.WorkingArea;
        var width = (int)(Width * scale);
        var height = (int)(Height * scale);
        Position = new PixelPoint(
            Math.Max(area.X, area.Right - width - 24),
            Math.Max(area.Y, area.Bottom - height - 24));
    }

    private void BringToTop()
    {
        if (!IsVisible) return;
        // 只调整 Z 序，不抢焦点，免得打断用户正在做的事
        Topmost = false;
        Topmost = true;
    }

    /// <summary>在 UI 线程隐藏。</summary>
    public void HideSafe()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible) Hide();
        });
    }
}
