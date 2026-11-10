using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MikuGuard.Services;

/// <summary>
/// 判断「选定的那台摄像头」此刻是不是真的被占用。
///
/// 做法只有一种：用「不允许别人共享」的方式打开设备接口路径。
///   - 打开成功 → 当时没有任何句柄占着它 → 空闲（占用的是别的摄像头）
///   - ERROR_SHARING_VIOLATION(32) / ERROR_LOCK_VIOLATION(33) → 正被占用
///   - 其它错误 → 判不出来（调用方按「被占用」处理）
///
/// **不要改回 DirectShow 建图试开**：2026-11-10 那版用 DirectShow 打开设备并起流，
/// 在设备被占用（Realtek VID_0BDA 摄像头 + 相机 App）时卡死在 RenderStream，
/// 3 秒超时后宿主进程整个崩掉（日志停在「触发告警」就没了）。取流那一步会把驱动搞崩，
/// 所以这里只做 CreateFile 共享检查——不碰驱动的取流路径，不亮 LED，也不会挂住线程。
/// </summary>
public static class CameraDeviceBusyProbe
{
    private const uint OpenExisting = 3;

    /// <summary>FILE_READ_ATTRIBUTES：KS 设备通常允许的最小权限。</summary>
    private const uint FileReadAttributes = 0x0080;

    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    /// <summary>
    /// 探测选定设备。
    /// </summary>
    /// <returns>true=被占用；false=空闲（占用的是别的摄像头）；null=判定不出来。</returns>
    public static bool? IsBusy(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;

        var path = Normalize(deviceId);
        if (path.Length == 0) return null;

        // 先按最小可读权限开；被拒再退到零权限开一次（有些驱动的设备对象不许要权限）
        return TryOpen(path, FileReadAttributes) ?? TryOpen(path, 0);
    }

    private static bool? TryOpen(string path, uint access)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = CreateFileW(path, access, 0, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle != null && !handle.IsInvalid)
            {
                GuardLog.Info("[probe] 能独占打开选定设备 → 它空闲（占用的是别的摄像头）");
                return false;
            }

            var err = Marshal.GetLastWin32Error();
            if (err == ErrorSharingViolation || err == ErrorLockViolation)
            {
                GuardLog.Info($"[probe] 共享冲突（{err}）→ 选定的摄像头正被占用");
                return true;
            }

            GuardLog.Info($"[probe] 打开失败，错误 {err}（权限 {access}）");
            return null;
        }
        catch (Exception ex)
        {
            GuardLog.Warn($"[probe] 共享检查异常：{ex.Message}");
            return null;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static string Normalize(string id)
    {
        var s = (id ?? "").Trim();
        const string pnpPrefix = "@device:pnp:";
        if (s.StartsWith(pnpPrefix, StringComparison.OrdinalIgnoreCase)) s = s[pnpPrefix.Length..];
        return s;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
}
