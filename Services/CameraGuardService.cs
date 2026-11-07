using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MikuGuard.Models;
using Timer = System.Timers.Timer;

namespace MikuGuard.Services;

/// <summary>
/// 摄像头守护主服务：轮询检测 → 过滤 → 告警 → 处理按钮动作。
/// </summary>
public sealed class CameraGuardService : ObservableObject, IDisposable
{
    private readonly PluginSettings _settings;
    private readonly CameraAlertService _alertService;
    private Timer? _timer;
    private bool _disposed;

    private bool _isCameraInUse;
    private string _activeAppName = "";
    private string _activeDeviceName = "";
    private string _statusText = "未启动";
    private string _lastActionResult = "";
    private DateTime _lastAlertUtc = DateTime.MinValue;
    private bool _suppressedUntilIdle;
    private bool _wasInUse;
    private string? _lastAppKey;

    public List<CameraDeviceInfo> Devices { get; private set; } = new();

    /// <summary>最近一次检测到的、正在使用摄像头的程序。</summary>
    public List<AppIdentity> DetectedApps { get; private set; } = new();

    public bool IsCameraInUse
    {
        get => _isCameraInUse;
        private set => SetProperty(ref _isCameraInUse, value);
    }

    public string ActiveAppName
    {
        get => _activeAppName;
        private set => SetProperty(ref _activeAppName, value);
    }

    public string ActiveDeviceName
    {
        get => _activeDeviceName;
        private set => SetProperty(ref _activeDeviceName, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string LastActionResult
    {
        get => _lastActionResult;
        private set => SetProperty(ref _lastActionResult, value);
    }

    public CameraAlertService AlertService => _alertService;

    public CameraGuardService(PluginSettings settings)
    {
        _settings = settings;
        _alertService = new CameraAlertService(settings);
        _alertService.ActionInvoked += HandleAction;
        _settings.Monitor.PropertyChanged += (_, _) => Restart();
        _settings.Alert.PropertyChanged += (_, _) => { };
    }

    public void Start(string? iconPath)
    {
        try
        {
            RefreshDevices();
            _alertService.Initialize(iconPath);
            Restart();
            GuardLog.Info($"[guard] 已启动（摄像头：{DescribeTarget()}，间隔 {_settings.Monitor.PollingIntervalMs}ms）");
        }
        catch (Exception ex)
        {
            GuardLog.Error("[guard] 启动失败", ex);
            StatusText = "启动失败：" + ex.Message;
        }
    }

    private string DescribeTarget() =>
        string.IsNullOrWhiteSpace(_settings.Monitor.DeviceId)
            ? "任意摄像头"
            : $"{_settings.Monitor.DeviceName}({_settings.Monitor.DeviceId})";

    public void RefreshDevices()
    {
        try
        {
            Devices = CameraDeviceService.Enumerate();
            GuardLog.Info($"[guard] 枚举到 {Devices.Count} 个摄像头：{string.Join("、", Devices.Select(d => d.Name))}");
        }
        catch (Exception ex)
        {
            GuardLog.Error("[guard] 枚举摄像头失败", ex);
            Devices = new List<CameraDeviceInfo>();
        }
    }

    public void Restart()
    {
        StopTimer();
        if (!_settings.Monitor.Enabled)
        {
            SetStatusAsync("已停用");
            return;
        }

        var interval = Math.Max(_settings.Monitor.PollingIntervalMs, 400);
        _timer = new Timer(interval) { AutoReset = true };
        _timer.Elapsed += (_, _) => Poll();
        _timer.Start();
        Poll();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }

    // ================= 轮询 =================

    private void Poll()
    {
        if (_disposed || !_settings.Monitor.Enabled) return;

        try
        {
            var consent = CameraConsentReader.ReadActive();
            var detection = Detect(consent);

            DetectedApps = consent;

            var appChanged = !string.Equals(detection.App?.Key, _lastAppKey, StringComparison.OrdinalIgnoreCase);
            var becameActive = detection.InUse && !_wasInUse;
            _wasInUse = detection.InUse;
            _lastAppKey = detection.App?.Key;

            if (!detection.InUse)
                _suppressedUntilIdle = false;

            UpdateState(detection);

            if (!detection.InUse)
            {
                _alertService.Dismiss();
                return;
            }

            if (!_suppressedUntilIdle && (becameActive || appChanged))
                TryAlert(detection);
        }
        catch (Exception ex)
        {
            GuardLog.Error("[guard] 轮询异常", ex);
        }
    }

    private Detection Detect(List<AppIdentity> consent)
    {
        var cfg = _settings.Monitor;
        var primary = consent.FirstOrDefault();
        var consentActive = consent.Count > 0;

        if (string.IsNullOrWhiteSpace(cfg.DeviceId))
            return new Detection(consentActive, primary, "", "", "");

        var device = Devices.FirstOrDefault(d => string.Equals(d.Id, cfg.DeviceId, StringComparison.OrdinalIgnoreCase));
        var instanceId = device?.InstanceId ?? "";

        if (!consentActive)
            return new Detection(false, null, cfg.DeviceName, instanceId, cfg.DeviceId);

        var occupied = CameraDeviceService.ProbeOccupied(cfg.DeviceId);

        bool inUse;
        if (occupied.HasValue)
            inUse = occupied.Value || (!cfg.StrictDeviceMatch && consentActive);
        else
            inUse = consentActive; // 探测不出来（驱动不支持）时退回按系统记录判断

        return new Detection(inUse, primary, cfg.DeviceName, instanceId, cfg.DeviceId);
    }

    private void TryAlert(Detection detection)
    {
        if (!_settings.Alert.Enabled) return;
        if (!ShouldAlert(detection.App)) return;

        var cooldown = Math.Max(_settings.Alert.CooldownSeconds, 0);
        if (cooldown > 0 && (DateTime.UtcNow - _lastAlertUtc).TotalSeconds < cooldown) return;

        _lastAlertUtc = DateTime.UtcNow;

        var alert = new GuardAlert(
            detection.App?.Key ?? "",
            detection.App?.DisplayName ?? "未知程序",
            detection.App?.IsPackaged ?? false,
            detection.DeviceName,
            detection.InstanceId,
            detection.DeviceId);

        GuardLog.Info($"[guard] 触发告警：{alert.AppDisplay} / {alert.DeviceName}");
        _alertService.Show(alert);
    }

    private bool ShouldAlert(AppIdentity? app)
    {
        var mode = _settings.Filter.Mode;
        if (mode == AppFilterMode.All) return true;

        var listed = app != null && IsListed(app);
        return mode == AppFilterMode.Blacklist ? !listed : listed;
    }

    private bool IsListed(AppIdentity app)
    {
        foreach (var entry in _settings.Filter.Entries)
        {
            var m = (entry.Match ?? "").Trim();
            if (m.Length == 0) continue;

            if (string.Equals(m, app.Key, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(m, app.DisplayName, StringComparison.OrdinalIgnoreCase)) return true;
            if (!app.IsPackaged && string.Equals(m, app.FileName, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private void UpdateState(Detection detection)
    {
        var appName = detection.App?.DisplayName ?? (detection.InUse ? "未知程序" : "");
        var status = detection.InUse
            ? $"{appName} 正在使用{(string.IsNullOrEmpty(detection.DeviceName) ? "摄像头" : "「" + detection.DeviceName + "」")}"
            : "摄像头空闲";

        Dispatcher.UIThread.Post(() =>
        {
            IsCameraInUse = detection.InUse;
            ActiveAppName = appName;
            ActiveDeviceName = detection.DeviceName;
            StatusText = status;
        });
    }

    private void SetStatusAsync(string text) => Dispatcher.UIThread.Post(() => StatusText = text);

    // ================= 按钮动作 =================

    private void HandleAction(string args)
    {
        var query = ParseQuery(args);
        if (!query.TryGetValue("action", out var action)) return;

        switch (action)
        {
            case "dismiss":
                _suppressedUntilIdle = true;
                _alertService.Dismiss();
                GuardLog.Info("[guard] 用户消除了提醒（本次使用期间不再打扰）");
                break;

            case "disablecamera":
                ThreadPool.QueueUserWorkItem(_ => DisableCamera(query));
                break;

            case "killapp":
                ThreadPool.QueueUserWorkItem(_ => KillApp(query));
                break;
        }
    }

    private void DisableCamera(Dictionary<string, string> query)
    {
        var inst = query.GetValueOrDefault("inst", "");
        var devId = query.GetValueOrDefault("dev", _settings.Monitor.DeviceId);
        var name = Devices.FirstOrDefault(d => string.Equals(d.Id, devId, StringComparison.OrdinalIgnoreCase))?.Name
                   ?? _settings.Monitor.DeviceName;

        if (string.IsNullOrEmpty(inst))
        {
            ReportResult("关闭摄像头失败", "没有拿到设备实例 ID，无法禁用");
            return;
        }

        var (ok, message) = CameraDeviceService.SetEnabled(inst, false);
        GuardLog.Info($"[guard] 关闭摄像头 {name}: {ok} {message}");

        if (ok)
        {
            ReportResult("摄像头已关闭", $"「{name}」已禁用，需要时可在 MikuGuard 设置页重新启用");
            RefreshDevices();
            return;
        }

        // 没权限禁用设备时，退而求其次：撤销正在用的那个程序的摄像头权限
        var app = DetectedApps.FirstOrDefault();
        if (app != null && CameraConsentReader.SetConsent(app, false))
        {
            ReportResult("已撤销摄像头权限", $"{message}；已改为禁止 {app.DisplayName} 访问摄像头");
            return;
        }

        ReportResult("关闭摄像头失败", message);
    }

    private void KillApp(Dictionary<string, string> query)
    {
        var key = query.GetValueOrDefault("app", "");
        var packaged = query.GetValueOrDefault("pkg", "0") == "1";

        if (string.IsNullOrEmpty(key))
        {
            ReportResult("关闭应用失败", "没有拿到程序信息");
            return;
        }

        var app = new AppIdentity
        {
            Key = key,
            DisplayName = packaged ? key : Path.GetFileName(key),
            IsPackaged = packaged
        };

        var (killed, message) = AppProcessService.Kill(app);
        GuardLog.Info($"[guard] 关闭应用 {key}: {killed} {message}");
        ReportResult(killed > 0 ? "已关闭应用" : "关闭应用失败", $"{app.DisplayName}：{message}");
    }

    private void ReportResult(string title, string message)
    {
        Dispatcher.UIThread.Post(() => LastActionResult = $"{title} — {message}");
        _alertService.Notify(title, message);
    }

    private static Dictionary<string, string> ParseQuery(string args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in args.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            var k = pair[..idx];
            var v = Uri.UnescapeDataString(pair[(idx + 1)..]);
            result[k] = v;
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTimer();
    }

    private readonly record struct Detection(bool InUse, AppIdentity? App, string DeviceName, string InstanceId,
        string DeviceId);
}
