using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MikuGuard.Services.Toast;

/// <summary>
/// Windows 通知（Toast）的身份注册：AUMID 注册表项 + 开始菜单快捷方式 + COM 激活器 CLSID。
/// 未打包的桌面程序要发 Toast，这三样都得到位。
/// </summary>
public static class ToastPlatform
{
    public const string Aumid = "xxtsoft.MikuGuard.CameraGuard";
    public static readonly Guid ActivatorClsid = new("8C1B7E44-9A3D-4C0E-9F52-3B6D1C7A5E10");

    private const string AumidRegPath = @"Software\Classes\AppUserModelId\" + Aumid;

    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Windows\Start Menu\Programs\MikuGuard 摄像头守护.lnk");

    /// <summary>注册是否完整（快捷方式 + AUMID + CLSID 都在）。</summary>
    public static bool IsRegistered { get; private set; }

    public static string StatusText { get; private set; } = "未注册";

    /// <summary>执行注册（幂等）。</summary>
    public static void EnsureRegistered(string? iconPath)
    {
        try
        {
            var exe = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exe))
            {
                StatusText = "拿不到宿主进程路径";
                return;
            }

            WriteAumidKey(iconPath);
            WriteActivatorKey(exe);
            WriteShortcut(exe, iconPath);

            IsRegistered = true;
            StatusText = "已注册";
            GuardLog.Info($"[toast] 通知身份已注册：{exe}");
        }
        catch (Exception ex)
        {
            IsRegistered = false;
            StatusText = $"注册失败：{ex.Message}";
            GuardLog.Error("[toast] 注册通知身份失败", ex);
        }
    }

    /// <summary>注销通知身份（删快捷方式与注册表项）。</summary>
    public static void Unregister()
    {
        try
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            Registry.CurrentUser.DeleteSubKeyTree(AumidRegPath, false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\{" + ActivatorClsid.ToString().ToUpperInvariant() + "}", false);
            IsRegistered = false;
            StatusText = "已注销";
            GuardLog.Info("[toast] 通知身份已注销");
        }
        catch (Exception ex)
        {
            GuardLog.Warn($"[toast] 注销失败：{ex.Message}");
        }
    }

    private static void WriteAumidKey(string? iconPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AumidRegPath);
        if (key == null) throw new InvalidOperationException("无法创建 AUMID 注册表项");
        key.SetValue("DisplayName", "MikuGuard");
        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            key.SetValue("IconUri", iconPath);
        key.SetValue("CustomActivator", "{" + ActivatorClsid.ToString().ToUpperInvariant() + "}");
    }

    private static void WriteActivatorKey(string exePath)
    {
        var path = @"Software\Classes\CLSID\{" + ActivatorClsid.ToString().ToUpperInvariant() + @"}\LocalServer32";
        using var key = Registry.CurrentUser.CreateSubKey(path);
        if (key == null) throw new InvalidOperationException("无法创建激活器注册表项");
        key.SetValue("", $"\"{exePath}\"");
        key.SetValue("ThreadingModel", "Both");
    }

    private static void WriteShortcut(string exePath, string? iconPath)
    {
        if (File.Exists(ShortcutPath))
        {
            var current = ShellShortcut.ReadAumid(ShortcutPath);
            if (string.Equals(current, Aumid, StringComparison.Ordinal))
            {
                // 已存在且 AUMID 正确，只在目标 exe 变化时重建
                if (string.Equals(ShellShortcut.ReadTarget(ShortcutPath), exePath, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        ShellShortcut.CreateWithAumid(ShortcutPath, exePath,
            !string.IsNullOrEmpty(iconPath) && File.Exists(iconPath) ? iconPath : exePath,
            "MikuGuard", Aumid);
    }

    // ================= Shell 快捷方式 =================

    private static class ShellShortcut
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPVARIANT
        {
            public ushort vt;
            public ushort r1;
            public ushort r2;
            public ushort r3;
            public IntPtr p;
            public IntPtr p2;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PROPERTYKEY pkey);
            void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
            void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
            void Commit();
        }

        private static readonly Guid PkeyAppUserModelId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        private const ushort VtLpwstr = 31;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHGetPropertyStoreFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr reserved, uint flags,
            ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        private static IPropertyStore OpenStore(string lnkPath, uint flags)
        {
            var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
            SHGetPropertyStoreFromParsingName(lnkPath, IntPtr.Zero, flags, ref iid, out var store);
            return store;
        }

        public static void CreateWithAumid(string lnkPath, string targetExe, string iconPath, string description,
            string aumid)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);

            var shellType = Type.GetTypeFromProgID("WScript" + ".Shell");
            if (shellType == null) throw new InvalidOperationException("无法创建快捷方式（WScript 不可用）");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = targetExe;
            lnk.WorkingDirectory = Path.GetDirectoryName(targetExe)!;
            lnk.IconLocation = iconPath + ",0";
            lnk.Description = description;
            lnk.Save();

            var store = OpenStore(lnkPath, 2 /*GPS_READWRITE*/);
            try
            {
                var key = new PROPERTYKEY { fmtid = PkeyAppUserModelId, pid = 5 };
                var ptr = Marshal.StringToCoTaskMemUni(aumid);
                var pv = new PROPVARIANT { vt = VtLpwstr, p = ptr };
                try { store.SetValue(ref key, ref pv); }
                finally { Marshal.FreeCoTaskMem(ptr); }
                store.Commit();
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }

            SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero);
        }

        public static string? ReadAumid(string lnkPath)
        {
            try
            {
                var store = OpenStore(lnkPath, 0);
                try
                {
                    var key = new PROPERTYKEY { fmtid = PkeyAppUserModelId, pid = 5 };
                    store.GetValue(ref key, out var pv);
                    return pv.vt == VtLpwstr && pv.p != IntPtr.Zero ? Marshal.PtrToStringUni(pv.p) : null;
                }
                finally
                {
                    Marshal.ReleaseComObject(store);
                }
            }
            catch
            {
                return null;
            }
        }

        public static string? ReadTarget(string lnkPath)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript" + ".Shell");
                if (shellType == null) return null;
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic lnk = shell.CreateShortcut(lnkPath);
                string target = lnk.TargetPath;
                return target;
            }
            catch
            {
                return null;
            }
        }
    }
}
