using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MikuGuard.Services;

/// <summary>按程序标识找到并结束进程。</summary>
public static class AppProcessService
{
    /// <summary>结束某个正在使用摄像头的程序，返回（结束数量, 说明）。</summary>
    public static (int Killed, string Message) Kill(AppIdentity app)
    {
        var killed = 0;
        var failed = new List<string>();

        try
        {
            if (app.IsPackaged)
            {
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        var aumid = GetPackagedAumid(proc.Id);
                        if (aumid == null ||
                            !aumid.StartsWith(app.Key + "!", StringComparison.OrdinalIgnoreCase)) continue;
                        if (TryKill(proc)) killed++;
                        else failed.Add($"{proc.ProcessName}(PID {proc.Id})");
                    }
                    catch
                    {
                        // 单个进程失败不影响整体
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            else
            {
                var name = Path.GetFileNameWithoutExtension(app.Key);
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (!PathMatches(proc, app.Key))
                        {
                            continue;
                        }

                        if (TryKill(proc)) killed++;
                        else failed.Add($"{proc.ProcessName}(PID {proc.Id})");
                    }
                    catch
                    {
                        // ignore
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            GuardLog.Error("[kill] 结束进程异常", ex);
            return (killed, $"结束进程失败：{ex.Message}");
        }

        if (killed > 0 && failed.Count == 0) return (killed, $"已结束 {killed} 个进程");
        if (killed > 0) return (killed, $"已结束 {killed} 个进程，{failed.Count} 个没有权限");
        if (failed.Count > 0) return (0, $"结束失败（权限不足）：{string.Join("、", failed)}");
        return (0, "没有找到正在运行的进程");
    }

    private static bool PathMatches(Process proc, string fullPath)
    {
        try
        {
            var main = proc.MainModule?.FileName;
            if (!string.IsNullOrEmpty(main))
                return string.Equals(main, fullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 跨位数 / 权限不足时拿不到 MainModule，退化为进程名匹配
        }

        return string.Equals(proc.ProcessName + ".exe", Path.GetFileName(fullPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryKill(Process proc)
    {
        try
        {
            proc.Kill();
            proc.WaitForExit(1500);
            GuardLog.Info($"[kill] 已结束 {proc.ProcessName} (PID {proc.Id})");
            return true;
        }
        catch (Exception ex)
        {
            GuardLog.Warn($"[kill] 结束 {proc.ProcessName} 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>读取某个进程的 AUMID（UWP 程序为 包族名!AppId）。</summary>
    private static string? GetPackagedAumid(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            uint len = 0;
            GetApplicationUserModelId(h, ref len, null);
            if (len == 0) return null;
            var buffer = new char[len];
            if (GetApplicationUserModelId(h, ref len, buffer) != 0) return null;
            var s = new string(buffer);
            var nul = s.IndexOf('\0');
            return nul >= 0 ? s[..nul] : s;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint applicationUserModelIdLength,
        [Out] char[]? applicationUserModelId);
}
