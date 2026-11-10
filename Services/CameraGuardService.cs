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

    // 「本轮真的报过警」才算数：只有它才会触发后面的「摄像头已关闭」提示
    private bool _alertRaisedThisSession;
    private GuardAlert? _lastAlert;
    private DateTime _inUseSinceUtc = DateTime.MinValue;

    // 设备探测结果缓存：同一批在用程序下只探一次（试开会短暂打开摄像头，不能每次都探）
    private string? _probeSignature;
    private string _probeDeviceId = "";
    private bool _probeBusy = true;
    private int _polling;

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

        // 设置变了就重新判一次设备占用，别拿旧结论压着
        _probeSignature = null;

        if (!_settings.Monitor.Enabled)
        {
            SetStatusAsync("已停用");
            return;
        }

        var interval = Math.Max(_settings.Monitor.PollingIntervalMs, 400);
        _timer = new Timer(interval) { AutoReset = true };
        _timer.Elapsed += (_, _) => Poll();
        _timer.Start();

        // 立刻先检测一次。设备探测会阻塞几秒，别挂在调用方（常常是 UI 线程）上
        ThreadPool.QueueUserWorkItem(_ => Poll());
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

        // 设备探测可能阻塞几秒，上一轮没跑完就跳过这一轮，别叠加
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;

        try
        {
            var consent = ReadActiveApps();
            var detection = Detect(consent);

            DetectedApps = consent;

            var appChanged = !string.Equals(detection.App?.Key, _lastAppKey, StringComparison.OrdinalIgnoreCase);
            var becameActive = detection.InUse && !_wasInUse;
            var becameIdle = !detection.InUse && _wasInUse;
            _wasInUse = detection.InUse;
            _lastAppKey = detection.App?.Key;

            if (becameActive)
            {
                _inUseSinceUtc = DateTime.UtcNow;
                _alertRaisedThisSession = false;
                _lastAlert = null;
            }

            if (!detection.InUse)
                _suppressedUntilIdle = false;

            UpdateState(detection);

            if (!detection.InUse)
            {
                if (becameIdle)
                {
                    _alertService.Dismiss();

                    // 只有「这次真的弹过占用提醒」才补一条解除提示，平时关掉摄像头不打扰
                    if (_alertRaisedThisSession && (DateTime.UtcNow - _inUseSinceUtc).TotalSeconds >= 1)
                        _alertService.ShowClosed(_lastAlert);

                    _alertRaisedThisSession = false;
                    _lastAlert = null;
                }

                return;
            }

            if (!_suppressedUntilIdle && (becameActive || appChanged))
                TryAlert(detection);
        }
        catch (Exception ex)
        {
            GuardLog.Error("[guard] 轮询异常", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    /// <summary>读系统访问记录，顺手剔掉宿主自己（预览窗口、探测试开都会记在 ClassIsland 头上）。</summary>
    private List<AppIdentity> ReadActiveApps()
    {
        var list = CameraConsentReader.ReadActive();

        var self = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(self))
            list.RemoveAll(a => !a.IsPackaged && string.Equals(a.Key, self, StringComparison.OrdinalIgnoreCase));

        foreach (var app in list) app.Ignored = !IsWatched(app);
        return list;
    }

    private Detection Detect(List<AppIdentity> consent)
    {
        var cfg = _settings.Monitor;
        var device = string.IsNullOrWhiteSpace(cfg.DeviceId)
            ? null
            : Devices.FirstOrDefault(d => string.Equals(d.Id, cfg.DeviceId, StringComparison.OrdinalIgnoreCase));

        var deviceName = string.IsNullOrWhiteSpace(cfg.DeviceId) ? "" : cfg.DeviceName;
        var instanceId = device?.InstanceId ?? "";

        // 第一层：被忽略的程序（黑名单内 / 白名单外）既不亮点也不提醒
        var watched = consent.Where(IsWatched).ToList();
        if (watched.Count == 0)
        {
            var note = consent.Count == 0
                ? ""
                : $"{consent[0].DisplayName} 正在使用摄像头（已忽略，不提示）";
            return new Detection(false, null, deviceName, instanceId, cfg.DeviceId, note);
        }

        // 第二层：选了具体某一台时，只有「它自己」真的被占用才算在用
        if (!string.IsNullOrWhiteSpace(cfg.DeviceId) && cfg.StrictDevice)
        {
            var signature = string.Join("|", consent.Select(a => a.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            if (!IsSelectedDeviceBusy(signature, cfg.DeviceId))
                return new Detection(false, null, deviceName, instanceId, cfg.DeviceId,
                    $"{watched[0].DisplayName} 正在使用其它摄像头（不是监视的这台，不提示）");
        }

        return new Detection(true, watched[0], deviceName, instanceId, cfg.DeviceId, "");
    }

    /// <summary>
    /// 选定摄像头是不是被占用的那台。试开一次（会短暂打开摄像头），结果按「当前在用程序集合」缓存。
    /// 判定不出来时按「被占用」处理：宁可多报，不用假信号压掉真实告警。
    /// </summary>
    private bool IsSelectedDeviceBusy(string signature, string deviceId)
    {
        if (string.Equals(_probeSignature, signature, StringComparison.Ordinal) &&
            string.Equals(_probeDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            return _probeBusy;

        _probeSignature = signature;
        _probeDeviceId = deviceId;

        var result = CameraDeviceBusyProbe.IsBusy(deviceId);
        _probeBusy = result ?? true;

        GuardLog.Info($"[guard] 设备探测「{_settings.Monitor.DeviceName}」：" + result switch
        {
            true => "正在被占用",
            false => "空闲（占用的是别的摄像头）",
            _ => "无法判定，按被占用处理"
        });

        return _probeBusy;
    }

    private void TryAlert(Detection detection)
    {
        if (!_settings.Alert.Enabled) return;
        if (!IsWatched(detection.App)) return;

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

        _alertRaisedThisSession = true;
        _lastAlert = alert;
    }

    /// <summary>
    /// 这个程序是否值得提示。黑名单模式下名单内=忽略；
    /// 白名单模式下不在名单里=忽略；「全部提醒」模式下都提示。
    /// </summary>
    private bool IsWatched(AppIdentity? app)
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
        var appName = detection.App?.DisplayName ?? "";
        var status = detection.InUse
            ? $"{appName} 正在使用{(string.IsNullOrEmpty(detection.DeviceName) ? "摄像头" : "「" + detection.DeviceName + "」")}"
            : !string.IsNullOrEmpty(detection.Note)
                ? detection.Note
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

            case "killapp":
                ThreadPool.QueueUserWorkItem(_ => KillApp(query));
                break;
        }
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

    /// <summary>
    /// 检测结果。InUse 是「要不要提示」的最终结论（被忽略的程序、非选定摄像头都已排除），
    /// Note 只是给状态栏解释为什么没提示。
    /// </summary>
    private readonly record struct Detection(bool InUse, AppIdentity? App, string DeviceName, string InstanceId,
        string DeviceId, string Note);
}
