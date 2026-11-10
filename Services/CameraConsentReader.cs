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

    /// <summary>
    /// 这个程序当前是否被忽略（黑名单内的、或白名单模式下不在名单里的）。
    /// 被忽略的程序既不亮指示点也不弹提醒，只用于设置页展示。
    /// </summary>
    public bool Ignored { get; set; }

    public override string ToString() => DisplayName;
}

/// <summary>
/// 读取 Windows「摄像头访问记录」注册表（ConsentStore），得到当前正在使用摄像头的程序。
/// 纯读取，不碰硬件。
///
/// 注意键的布局（Windows 10/11 实况）：
///   webcam\NonPackaged\C:#Program Files#Foo#Foo.exe   ← 桌面程序（\ 写成 #）
///   webcam\Microsoft.WindowsCamera_8wekyb3d8bbwe       ← UWP 程序直接挂在根下（没有 Packages 层级！）
/// 早期版本还有 webcam\Packages\{包族名} 的写法，这里一并兼容。
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

            foreach (var subName in root.GetSubKeyNames())
            {
                if (subName.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    ReadDesktopApps(root, subName, list);
                }
                else if (subName.Equals("Packages", StringComparison.OrdinalIgnoreCase))
                {
                    // 部分系统版本会把 UWP 放在 Packages 下
                    using var packages = root.OpenSubKey(subName);
                    if (packages == null) continue;
                    foreach (var appName in packages.GetSubKeyNames())
                        ReadAppKey(packages, appName, true, list);
                }
                else
                {
                    // 直接挂在 webcam 下的 UWP 包（Windows 相机就属于这种）
                    ReadAppKey(root, subName, true, list);
                }
            }
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

    /// <summary>判断某个程序当前是否被系统允许访问摄像头。</summary>
    public static bool? ReadConsentValue(AppIdentity app)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ConsentKeyPath(app));
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
            using var key = Registry.CurrentUser.CreateSubKey(ConsentKeyPath(app));
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

    private static string ConsentKeyPath(AppIdentity app) =>
        app.IsPackaged
            ? WebcamRoot + "\\" + app.Key
            : WebcamRoot + @"\NonPackaged\" + app.Key.Replace('\\', '#').Replace('/', '#');

    private static void ReadDesktopApps(RegistryKey parent, string subName, List<AppIdentity> output)
    {
        using var sub = parent.OpenSubKey(subName);
        if (sub == null) return;

        foreach (var appName in sub.GetSubKeyNames())
        {
            // 桌面程序的键名用 # 代替 \，还原成真实路径
            ReadAppKey(sub, appName.Replace('#', '\\'), false, output, subKeyName: appName);
        }
    }

    /// <summary>读一个应用键；subKeyName 为空时用 appKeyName 本身作为注册表子键名。</summary>
    private static void ReadAppKey(RegistryKey parent, string appKeyName, bool packaged,
        List<AppIdentity> output, string? subKeyName = null)
    {
        using var appKey = parent.OpenSubKey(subKeyName ?? appKeyName);
        if (appKey == null) return;

        var start = ReadInt64(appKey, "LastUsedTimeStart");
        var stop = ReadInt64(appKey, "LastUsedTimeStop");
        if (start <= 0) return;      // 从未用过
        if (stop != 0) return;       // 已经结束

        if (!packaged && !IsDesktopProcessAlive(appKeyName)) return;   // 进程已退出，记录是脏的

        output.Add(new AppIdentity
        {
            Key = appKeyName,
            DisplayName = packaged ? PackageDisplayName(appKeyName) : Path.GetFileName(appKeyName),
            IsPackaged = packaged,
            StartedAt = FromFileTimeUtc(start)
        });
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
        // 包族名对用户没意义（比如 Microsoft.WindowsCamera_8wekyb3d8bbwe），
        // 查一下包的显示名（"Windows 相机"），查不到再退回包名。
        try
        {
            var pkg = new Windows.Management.Deployment.PackageManager().FindPackage(familyName);
            if (pkg != null && !string.IsNullOrWhiteSpace(pkg.DisplayName))
                return pkg.DisplayName;
        }
        catch
        {
            // 查不到就算了
        }

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
