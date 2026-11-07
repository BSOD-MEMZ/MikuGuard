using Microsoft.Win32;

namespace MikuGuard.Services;

/// <summary>一个正在使用摄像头的程序。</summary>
public sealed class AppIdentity
{
    /// <summary>系统标识：桌面程序是完整 exe 路径，UWP 是包族名。</summary>
    public string Key { get; init; } = "";

    /// <summary>展示用名称。</summary>
    public string DisplayName { get; init; } = "";

    public bool IsPackaged { get; init; }

    /// <summary>开始使用的时间（排序用）。</summary>
    public DateTime? StartedAt { get; init; }

    /// <summary>exe 文件名（桌面程序，过滤名单用）。</summary>
    public string FileName =>
        IsPackaged ? "" : Path.GetFileName(Key);

    public override string ToString() => DisplayName;
}

/// <summary>
/// 读取 Windows「摄像头访问记录」注册表（ConsentStore），得到当前正在使用摄像头的程序。
/// 纯读取，不碰硬件。
/// </summary>
public static class CameraConsentReader
{
    private const string WebcamRoot =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    /// <summary>列出当前处于「使用中」状态的程序，按开始时间倒序。</summary>
    public static List<AppIdentity> ReadActive()
    {
        var list = new List<AppIdentity>();

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(WebcamRoot);
            if (root == null) return list;

            ReadSubKey(root, "NonPackaged", false, list);
            ReadSubKey(root, "Packages", true, list);
        }
        catch (Exception ex)
        {
            GuardLog.Warn($"[consent] 读取失败：{ex.Message}");
        }

        return list
            .Where(x => !string.IsNullOrEmpty(x.Key))
            .OrderByDescending(x => x.StartedAt ?? DateTime.MinValue)
            .ToList();
    }

    /// <summary>判断某个程序当前是否被系统允许访问摄像头（用于撤销权限后回读）。</summary>
    public static bool? ReadConsentValue(AppIdentity app)
    {
        try
        {
            var sub = WebcamRoot + (app.IsPackaged ? @"\Packages\" : @"\NonPackaged\") + EscapeKey(app);
            using var key = Registry.CurrentUser.OpenSubKey(sub);
            var v = key?.GetValue("Value") as string;
            if (v == null) return null;
            return !v.Equals("Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>撤销 / 恢复某程序的摄像头访问权限（写入 ConsentStore 的 Value）。</summary>
    public static bool SetConsent(AppIdentity app, bool allow)
    {
        try
        {
            var sub = WebcamRoot + (app.IsPackaged ? @"\Packages\" : @"\NonPackaged\") + EscapeKey(app);
            using var key = Registry.CurrentUser.CreateSubKey(sub);
            if (key == null) return false;
            key.SetValue("Value", allow ? "Allow" : "Deny", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            GuardLog.Warn($"[consent] 写入失败：{ex.Message}");
            return false;
        }
    }

    private static string EscapeKey(AppIdentity app)
    {
        if (app.IsPackaged) return app.Key;
        // 注册表键名里 \ 被替换成 #
        return app.Key.Replace('\\', '#').Replace('/', '#');
    }

    private static void ReadSubKey(RegistryKey parent, string name, bool packaged, List<AppIdentity> output)
    {
        using var sub = parent.OpenSubKey(name);
        if (sub == null) return;

        foreach (var appName in sub.GetSubKeyNames())
        {
            using var appKey = sub.OpenSubKey(appName);
            if (appKey == null) continue;

            var start = ReadInt64(appKey, "LastUsedTimeStart");
            var stop = ReadInt64(appKey, "LastUsedTimeStop");
            if (start <= 0) continue;         // 从未用过
            if (stop != 0) continue;          // 已经结束

            var key = packaged ? appName : appName.Replace('#', '\\');
            var started = FromFileTimeUtc(start);

            if (!packaged && !IsDesktopProcessAlive(key)) continue;   // 进程已退出，记录是脏的

            output.Add(new AppIdentity
            {
                Key = key,
                DisplayName = packaged ? PackageDisplayName(key) : Path.GetFileName(key),
                IsPackaged = packaged,
                StartedAt = started
            });
        }
    }

    private static bool IsDesktopProcessAlive(string exePath)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(exePath);
            if (string.IsNullOrWhiteSpace(name)) return false;
            var procs = System.Diagnostics.Process.GetProcessesByName(name);
            try
            {
                return procs.Length > 0;
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        catch
        {
            return true;
        }
    }

    private static string PackageDisplayName(string familyName)
    {
        var bang = familyName.IndexOf('_');
        return bang > 0 ? familyName[..bang] : familyName;
    }

    private static long ReadInt64(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        return value switch
        {
            long l => l,
            int i => i,
            uint u => u,
            _ => 0
        };
    }

    private static DateTime? FromFileTimeUtc(long fileTime)
    {
        try
        {
            if (fileTime <= 0) return null;
            return DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
        }
        catch
        {
            return null;
        }
    }
}
