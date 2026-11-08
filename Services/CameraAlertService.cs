using Avalonia.Threading;
using MikuGuard.Models;
using MikuGuard.Services.Alert;
using MikuGuard.Services.Toast;

namespace MikuGuard.Services;

/// <summary>一次告警的上下文。</summary>
public sealed record GuardAlert(
    string AppKey,
    string AppDisplay,
    bool IsPackaged,
    string DeviceName,
    string DeviceInstanceId,
    string DeviceId);

/// <summary>把告警送到用户眼前：优先 Windows 通知，失败或用户指定时用插件内弹窗。</summary>
public sealed class CameraAlertService
{
    private readonly PluginSettings _settings;
    private readonly WindowsToastPresenter _toast = new();
    private GuardAlertWindow? _window;

    /// <summary>按钮被点击（参数与 Toast arguments 同格式）。</summary>
    public event Action<string>? ActionInvoked;

    public CameraAlertService(PluginSettings settings) => _settings = settings;

    public bool ToastAvailable => _toast.Available;

    public string ToastError => _toast.LastError;

    public string RegistrationStatus => ToastPlatform.StatusText;

    public void Initialize(string? iconPath)
    {
        ToastPlatform.EnsureRegistered(iconPath);
        ToastActivator.Register(OnToastActivated);
        _toast.TryInitialize();
    }

    private void OnToastActivated(string args)
    {
        // COM 回调线程 → 丢到线程池交给业务处理
        ThreadPool.QueueUserWorkItem(_ => ActionInvoked?.Invoke(args));
    }

    /// <summary>弹出告警。</summary>
    public void Show(GuardAlert alert)
    {
        var actions = BuildActions(alert);
        var title = string.IsNullOrWhiteSpace(_settings.Alert.Title) ? "摄像头正在被使用" : _settings.Alert.Title;
        var body = BuildBody(alert);

        var image = ResolveImagePath();
        var preferToast = _settings.Alert.Presentation != AlertPresentation.InApp;
        if (preferToast && _toast.TryShow(title, body, actions, image))
        {
            HideWindow();
            return;
        }

        if (_settings.Alert.Presentation == AlertPresentation.WindowsToast)
        {
            // 明确只用 Toast 却发不出去：至少留个日志，不再弹窗打扰
            GuardLog.Warn("[alert] 指定只用 Windows 通知，但发送失败，本次不提示");
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _window ??= CreateWindow();
                _window.Present(title, body, actions, image);
            }
            catch (Exception ex)
            {
                GuardLog.Error("[alert] 弹窗显示失败", ex);
            }
        });
    }

    /// <summary>撤掉当前提示。</summary>
    public void Dismiss()
    {
        _toast.Dismiss();
        HideWindow();
    }

    /// <summary>发送一条纯文本回执（比如「摄像头已关闭」）。</summary>
    public void Notify(string title, string body)
    {
        if (!_settings.Alert.ShowActionResult) return;

        if (_settings.Alert.Presentation != AlertPresentation.InApp &&
            _toast.TryShow(title, body, Array.Empty<ToastActionButton>()))
            return;

        // Toast 不可用时用弹窗短暂提示，3 秒后自动收起
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _window ??= CreateWindow();
                _window.Present(title, body, Array.Empty<ToastActionButton>(), null);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    _window?.HideSafe();
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                GuardLog.Error("[alert] 回执弹窗失败", ex);
            }
        });
    }

    /// <summary>解析配图路径：相对路径按插件目录算，文件不存在就当没有。</summary>
    private string? ResolveImagePath()
    {
        try
        {
            var full = MikuGuardPaths.Resolve(_settings.Alert.ImagePath);
            if (full.Length == 0) return null;

            if (!File.Exists(full))
                GuardLog.Warn($"[alert] 配图不存在：{full}");

            return File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    private GuardAlertWindow CreateWindow()
    {
        var window = new GuardAlertWindow();
        window.ActionInvoked += args => ActionInvoked?.Invoke(args);
        return window;
    }

    private void HideWindow()
    {
        _window?.HideSafe();
    }

    private List<ToastActionButton> BuildActions(GuardAlert alert)
    {
        var list = new List<ToastActionButton>();
        var cfg = _settings.Alert;

        if (cfg.ShowDismissAction)
            list.Add(new ToastActionButton("消除提醒", "action=dismiss"));

        if (cfg.ShowCloseAppAction && !string.IsNullOrEmpty(alert.AppKey))
            list.Add(new ToastActionButton("关闭应用",
                $"action=killapp&app={Uri.EscapeDataString(alert.AppKey)}&pkg={(alert.IsPackaged ? "1" : "0")}"));

        return list;
    }

    private static string BuildBody(GuardAlert alert)
    {
        var app = string.IsNullOrWhiteSpace(alert.AppDisplay) ? "某个程序" : alert.AppDisplay;
        var device = string.IsNullOrWhiteSpace(alert.DeviceName) ? "摄像头" : $"{alert.DeviceName}";
        return $"{app} 正在使用{device}，请注意隐私安全！";
    }
}
