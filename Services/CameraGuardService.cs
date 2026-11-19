using System.Diagnostics;
using System.Runtime.InteropServices;
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

            if (!CameraDeviceBusyProbe.Start())
                GuardLog.Warn("[guard] 摄像头活动监控启动失败，退回注册表检测（新系统上可能读不到）");

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
            var active = ReadActiveApps();
            var detection = Detect(active);

            DetectedApps = active.Select(a => a.App).ToList();

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

    /// <summary>
    /// 取「当前正在使用摄像头」的程序。主路径是 Media Foundation 传感器活动监控
    /// （设备 + pid 都是系统给的），监控起不来时才退回旧的 ConsentStore 记录。
    /// </summary>
    private List<ActiveApp> ReadActiveApps()
    {
        var result = new List<ActiveApp>();

        foreach (var activity in CameraDeviceBusyProbe.Snapshot())
        {
            foreach (var pid in activity.ProcessIds)
            {
                var app = ToAppIdentity(pid);
                if (app is null) continue;
                if (result.Any(r => string.Equals(r.App.Key, app.Key, StringComparison.OrdinalIgnoreCase))) continue;

                app.Ignored = !IsWatched(app);
                result.Add(new ActiveApp(app, activity.DeviceId, activity.DeviceName));
            }
        }

        // 传感器监控不可用（老系统 / MF 初始化失败）时退回注册表：24H2 之后系统不再写这个记录
        if (result.Count == 0 && !CameraDeviceBusyProbe.IsRunning)
        {
            var self = Environment.ProcessPath;
            foreach (var app in CameraConsentReader.ReadActive())
            {
                if (!string.IsNullOrEmpty(self) && !app.IsPackaged &&
                    string.Equals(app.Key, self, StringComparison.OrdinalIgnoreCase)) continue;

                app.Ignored = !IsWatched(app);
                result.Add(new ActiveApp(app, _settings.Monitor.DeviceId, _settings.Monitor.DeviceName));
            }
        }

        return result;
    }

    private Detection Detect(List<ActiveApp> active)
    {
        var cfg = _settings.Monitor;
        var watched = active.Where(a => IsWatched(a.App)).ToList();

        // 选定了具体设备且要求严格匹配时，只看那台摄像头的占用
        if (!string.IsNullOrWhiteSpace(cfg.DeviceId) && cfg.StrictDevice)
        {
            watched = watched
                .Where(a => string.Equals(a.DeviceId, cfg.DeviceId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var preferred = watched.FirstOrDefault();
        var deviceId = preferred.DeviceId ?? cfg.DeviceId;
        var deviceName = !string.IsNullOrWhiteSpace(preferred.DeviceName) ? preferred.DeviceName : cfg.DeviceName;
        var device = Devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        var instanceId = device?.InstanceId ?? "";

        if (preferred.App is null)
        {
            var note = "";
            if (active.Count > 0)
            {
                note = !string.IsNullOrWhiteSpace(cfg.DeviceId) && cfg.StrictDevice
                    ? $"{active[0].App.DisplayName} 正在使用其它摄像头（不是监视的这台，不提示）"
                    : $"{active[0].App.DisplayName} 正在使用摄像头（已忽略，不提示）";
            }

            return new Detection(false, null, deviceName, instanceId, cfg.DeviceId, note);
        }

        return new Detection(true, preferred.App, deviceName, instanceId, deviceId, "");
    }

    /// <summary>把「正在用摄像头的进程」还原成插件里的程序身份（桌面程序用 exe 路径，UWP 用包族名）。</summary>
    private static AppIdentity? ToAppIdentity(uint pid)
    {
        if (pid == 0 || pid == (uint)Environment.ProcessId) return null;

        Process? process = null;
        try
        {
            process = Process.GetProcessById((int)pid);
            var processName = process.ProcessName;

            string path = "";
            try { path = process.MainModule?.FileName ?? ""; } catch { }

            if (path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            {
                var family = GetPackageFamilyName(process);
                var key = family.Length > 0 ? family : processName;
                return new AppIdentity
                {
                    Key = key,
                    DisplayName = PackagedDisplayName(key) ?? processName,
                    IsPackaged = family.Length > 0,
                    StartedAt = TryStartTime(process)
                };
            }

            return new AppIdentity
            {
                Key = path.Length > 0 ? path : processName,
                DisplayName = path.Length > 0 ? Path.GetFileName(path) : processName,
                IsPackaged = false,
                StartedAt = TryStartTime(process)
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static DateTime? TryStartTime(Process process)
    {
        try { return process.StartTime; }
        catch { return null; }
    }

    private static string GetPackageFamilyName(Process process)
    {
        try
        {
            uint length = 0;
            var rc = GetPackageFamilyNameNative(process.Handle, ref length, null);
            if (rc != 122 || length == 0) return "";   // 122 = ERROR_INSUFFICIENT_BUFFER

            var buffer = new char[length];
            rc = GetPackageFamilyNameNative(process.Handle, ref length, buffer);
            return rc == 0 ? new string(buffer).TrimEnd('\0') : "";
        }
        catch
        {
            return "";
        }
    }

    private static string? PackagedDisplayName(string familyOrName)
    {
        try
        {
            var package = new Windows.Management.Deployment.PackageManager().FindPackage(familyOrName);
            if (package != null && !string.IsNullOrWhiteSpace(package.DisplayName)) return package.DisplayName;
        }
        catch
        {
            // 包已经卸载 / 不是包族名
        }

        var bang = familyOrName.IndexOf('_');
        return bang > 0 ? familyOrName[..bang] : null;
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
        CameraDeviceBusyProbe.Stop();
    }

    /// <summary>一条「程序正在用某台摄像头」的记录。</summary>
    private readonly record struct ActiveApp(AppIdentity App, string DeviceId, string DeviceName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetPackageFamilyNameNative(IntPtr hProcess, ref uint packageFamilyNameLength,
        [Out] char[]? packageFamilyName);

    /// <summary>
    /// 检测结果。InUse 是「要不要提示」的最终结论（被忽略的程序、非选定摄像头都已排除），
    /// Note 只是给状态栏解释为什么没提示。
    /// </summary>
    private readonly record struct Detection(bool InUse, AppIdentity? App, string DeviceName, string InstanceId,
        string DeviceId, string Note);
}
