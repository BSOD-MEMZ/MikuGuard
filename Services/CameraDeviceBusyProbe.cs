using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace MikuGuard.Services;

/// <summary>某台摄像头当前的占用情况。</summary>
public sealed record CameraDeviceActivity(string DeviceId, string DeviceName, IReadOnlyList<uint> ProcessIds)
{
    public bool IsBusy => ProcessIds.Count > 0;
}

/// <summary>
/// 摄像头占用探测：走 Media Foundation 的「传感器活动监控」（IMFSensorActivityMonitor）。
///
/// 为什么不再用注册表 / CreateFile：
///   1. Windows 11 24H2（26100）起，桌面程序用摄像头时系统不再更新
///      ConsentStore\webcam 的 LastUsedTimeStart/Stop，读注册表永远为空（2026-11-19 实测：
///      MediaCapture 取流 25 秒，HKCU 下一条记录都没有）。
///   2. 摄像头现在由 FrameServer 服务统一取流，客户端进程根本不持有设备句柄，
///      所以「用不共享方式 CreateFile 打开设备接口」也永远成功，判不出占用（同批实测）。
///   3. MF 传感器活动监控是系统给「谁在用摄像头」准备的正式接口：
///      每台设备上报一串 (pid, streaming)，正好是插件需要的信息，还能精确到设备。
///
/// 注意：
///   - 回调在 COM 线程上来，所有共享状态都加锁。
///   - 报告是「当前活动快照」，没有活动的设备不出现在报告里；收到报告就整体替换。
///   - 必须 MFStartup 成功后才能 MFCreateSensorActivityMonitor；插件退出时 Stop 并 MFShutdown。
/// </summary>
public static class CameraDeviceBusyProbe
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, CameraDeviceActivity> Active =
        new(StringComparer.OrdinalIgnoreCase);

    private static ActivityCallback? _callback;
    private static IMFSensorActivityMonitor? _monitor;
    private static bool _started;

    /// <summary>监控是否已经启动。</summary>
    public static bool IsRunning
    {
        get { lock (Gate) return _started; }
    }

    /// <summary>启动监控（幂等）。</summary>
    public static bool Start()
    {
        lock (Gate)
        {
            if (_started) return true;

            try
            {
                var hr = PInvoke.MFStartup(PInvoke.MF_VERSION, 0);
                if (hr.Failed)
                {
                    GuardLog.Warn($"[sensor] MFStartup 失败：0x{hr.Value:X8}");
                    return false;
                }

                _callback = new ActivityCallback(OnReport);
                hr = PInvoke.MFCreateSensorActivityMonitor(_callback, out var monitor);
                if (hr.Failed || monitor is null)
                {
                    GuardLog.Warn($"[sensor] 创建活动监控失败：0x{hr.Value:X8}");
                    _callback = null;
                    PInvoke.MFShutdown();
                    return false;
                }

                monitor.Start();
                _monitor = monitor;
                _started = true;
                GuardLog.Info("[sensor] Media Foundation 摄像头活动监控已启动");
                return true;
            }
            catch (Exception ex)
            {
                GuardLog.Error("[sensor] 启动活动监控异常", ex);
                return false;
            }
        }
    }

    /// <summary>停止监控（幂等）。</summary>
    public static void Stop()
    {
        IMFSensorActivityMonitor? monitor;
        lock (Gate)
        {
            if (!_started) return;
            monitor = _monitor;
            _monitor = null;
            _callback = null;
            _started = false;
            Active.Clear();
        }

        // Stop 放锁外：它可能等回调线程收尾，而回调里要拿 Gate，锁内调用会互相等死
        try { monitor?.Stop(); }
        catch (Exception ex) { GuardLog.Warn($"[sensor] 停止活动监控异常：{ex.Message}"); }

        try { PInvoke.MFShutdown(); }
        catch (Exception ex) { GuardLog.Warn($"[sensor] MFShutdown 异常：{ex.Message}"); }
    }

    /// <summary>取当前快照：只包含正在被占用的设备。</summary>
    public static IReadOnlyList<CameraDeviceActivity> Snapshot()
    {
        lock (Gate)
        {
            return Active.Values
                .Where(a => a.IsBusy)
                .Select(a => new CameraDeviceActivity(a.DeviceId, a.DeviceName, a.ProcessIds.ToArray()))
                .ToList();
        }
    }

    private static void OnReport(IMFSensorActivitiesReport report)
    {
        try
        {
            report.GetCount(out var count);

            var next = new Dictionary<string, CameraDeviceActivity>(StringComparer.OrdinalIgnoreCase);
            for (uint i = 0; i < count; i++)
            {
                report.GetActivityReport(i, out var deviceReport);
                if (deviceReport is null) continue;

                var deviceId = ReadDeviceString(buffer => deviceReport.GetSymbolicLink(buffer, out _));
                if (deviceId.Length == 0) continue;

                var deviceName = ReadDeviceString(buffer => deviceReport.GetFriendlyName(buffer, out _));
                deviceReport.GetProcessCount(out var processCount);

                var pids = new List<uint>();
                for (uint p = 0; p < processCount; p++)
                {
                    deviceReport.GetProcessActivity(p, out var process);
                    if (process is null) continue;

                    process.GetStreamingState(out BOOL streaming);
                    if (!streaming) continue;

                    process.GetProcessId(out var pid);
                    if (pid > 0) pids.Add(pid);
                }

                next[deviceId] = new CameraDeviceActivity(deviceId, deviceName, pids);
            }

            lock (Gate)
            {
                Active.Clear();
                foreach (var pair in next) Active[pair.Key] = pair.Value;
            }
        }
        catch (Exception ex)
        {
            // 设备刚好被拔掉 / 报告过期时会抛，忽略即可，下次报告会覆盖
            GuardLog.Warn($"[sensor] 处理活动报告失败：{ex.Message}");
        }
    }

    private static string ReadDeviceString(Action<char[]> reader)
    {
        var buffer = new char[520];
        try { reader(buffer); }
        catch { return ""; }

        var text = new string(buffer);
        var nul = text.IndexOf('\0');
        return (nul >= 0 ? text[..nul] : text).Trim();
    }

    /// <summary>COM 回调实现：MF 在活动变化时回调它。</summary>
    private sealed class ActivityCallback : IMFSensorActivitiesReportCallback
    {
        private readonly Action<IMFSensorActivitiesReport> _onReport;

        public ActivityCallback(Action<IMFSensorActivitiesReport> onReport) => _onReport = onReport;

        public void OnActivitiesReport(IMFSensorActivitiesReport sensorActivitiesReport)
        {
            try { _onReport(sensorActivitiesReport); }
            catch (Exception ex) { GuardLog.Warn($"[sensor] 回调异常：{ex.Message}"); }
        }
    }
}
