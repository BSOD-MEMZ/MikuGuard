using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace MikuGuard.Services;

/// <summary>一个摄像头设备。</summary>
public sealed class CameraDeviceInfo
{
    /// <summary>设备接口路径（\\?\usb#vid_...#{guid}），作为稳定 id。</summary>
    public string Id { get; init; } = "";

    /// <summary>设备实例 ID（USB\VID_...\...），禁用设备时用。</summary>
    public string InstanceId { get; init; } = "";

    /// <summary>显示名称。</summary>
    public string Name { get; set; } = "";

    public override string ToString() => string.IsNullOrEmpty(Name) ? Id : Name;
}

/// <summary>
/// 摄像头设备枚举 / 占用探测 / 启停。
/// 设备名走 cfgmgr32 + 注册表（纯读，零副作用）；占用探测走 DirectShow 设备绑定
/// （绑定失败即说明设备被别人独占）。
/// </summary>
public static class CameraDeviceService
{
    private static readonly Guid DevInterfaceVideoCamera = new("E5323777-F976-4F5B-9B55-B94699C46E44");
    private static readonly Guid DevInterfaceKsVideo = new("6994AD05-93EF-11D0-A3CC-00A0C9223196");
    private static readonly Guid ClsidVideoInputDeviceCategory = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");
    private static readonly Guid ClsidSystemDeviceEnum = new("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");

    private const uint CmGetDeviceInterfaceListPresent = 0;

    // ================= 枚举 =================

    /// <summary>枚举当前存在的摄像头设备（同一设备的多余接口会被合并）。</summary>
    public static List<CameraDeviceInfo> Enumerate()
    {
        var result = new List<CameraDeviceInfo>();
        var seenPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenInstance = new Dictionary<string, CameraDeviceInfo>(StringComparer.OrdinalIgnoreCase);

        // 先枚举「摄像头」类接口（显示名最准），再补「视频采集」类里没覆盖到的设备
        foreach (var guid in new[] { DevInterfaceVideoCamera, DevInterfaceKsVideo })
        {
            foreach (var path in EnumerateInterfaces(guid))
            {
                if (!seenPath.Add(path)) continue;

                var instanceId = ToInstanceId(path);
                if (seenInstance.ContainsKey(instanceId)) continue;

                var device = new CameraDeviceInfo
                {
                    Id = path,
                    InstanceId = instanceId,
                    Name = ReadFriendlyName(instanceId)
                };
                seenInstance[instanceId] = device;
                result.Add(device);
            }
        }

        // 同名设备加序号，方便在设置页区分前后摄像头
        foreach (var group in result.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var index = 1;
            foreach (var device in group)
                device.Name = $"{device.Name} ({index++})";
        }

        return result;
    }

    private static IEnumerable<string> EnumerateInterfaces(Guid classGuid)
    {
        uint len = 0;
        var g = classGuid;
        if (CM_Get_Device_Interface_List_SizeW(out len, ref g, null, CmGetDeviceInterfaceListPresent) != 0 || len <= 1)
            yield break;

        var buffer = new char[len];
        g = classGuid;
        if (CM_Get_Device_Interface_ListW(ref g, null, buffer, len, CmGetDeviceInterfaceListPresent) != 0)
            yield break;

        var joined = new string(buffer);
        foreach (var item in joined.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            yield return item;
    }

    /// <summary>把接口路径转成设备实例 ID：\\?\usb#vid_x#y#{guid} -&gt; USB\VID_X\Y</summary>
    public static string ToInstanceId(string interfacePath)
    {
        var p = interfacePath;
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        var hashGuid = p.IndexOf("#{", StringComparison.Ordinal);
        if (hashGuid > 0) p = p[..hashGuid];
        if (p.EndsWith("#", StringComparison.Ordinal)) p = p[..^1];
        return p.Replace('#', '\\');
    }

    private static string ReadFriendlyName(string instanceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instanceId);
            if (key != null)
            {
                if (key.GetValue("FriendlyName") is string fn && !string.IsNullOrWhiteSpace(fn)) return fn.Trim();
                if (key.GetValue("DeviceDesc") is string desc && !string.IsNullOrWhiteSpace(desc))
                {
                    var semi = desc.LastIndexOf(';');
                    if (semi >= 0 && semi + 1 < desc.Length) desc = desc[(semi + 1)..];
                    return desc.Trim();
                }
            }
        }
        catch
        {
            // 忽略
        }

        var slash = instanceId.LastIndexOf('\\');
        return slash >= 0 ? instanceId[(slash + 1)..] : instanceId;
    }

    // ================= 占用探测 =================

    /// <summary>
    /// 探测指定摄像头是否被占用。
    /// 返回 true = 被占用；false = 空闲；null = 无法判断（设备不在、驱动异常等）。
    /// </summary>
    public static bool? ProbeOccupied(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        var target = NormalizePath(deviceId);

        var monikers = new List<IMoniker>();
        try
        {
            CollectMonikers(monikers);

            foreach (var moniker in monikers)
            {
                string display;
                try { moniker.GetDisplayName(null, null, out display); }
                catch { continue; }

                if (!string.Equals(NormalizePath(display), target, StringComparison.OrdinalIgnoreCase))
                    continue;

                object? filter = null;
                try
                {
                    var iid = typeof(IBaseFilter).GUID;
                    moniker.BindToObject(null, null, ref iid, out filter);
                    return false; // 能绑上 → 设备空闲
                }
                catch (Exception ex)
                {
                    GuardLog.Info($"[device] {deviceId} 绑定失败，判定为占用：0x{ex.HResult:X8}");
                    return true; // 绑不上 → 被别人独占
                }
                finally
                {
                    if (filter != null) Marshal.ReleaseComObject(filter);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            GuardLog.Warn($"[device] 探测失败：{ex.Message}");
            return null;
        }
        finally
        {
            foreach (var m in monikers) Marshal.ReleaseComObject(m);
        }
    }

    private static void CollectMonikers(List<IMoniker> output)
    {
        object? devEnumObj = null;
        IEnumMoniker? enumMoniker = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidSystemDeviceEnum);
            if (type == null) return;
            devEnumObj = Activator.CreateInstance(type);
            if (devEnumObj is not ICreateDevEnum devEnum) return;

            var cat = ClsidVideoInputDeviceCategory;
            if (devEnum.CreateClassEnumerator(ref cat, out enumMoniker, 0) != 0) return;

            var buffer = new IMoniker[1];
            while (enumMoniker.Next(1, buffer, IntPtr.Zero) == 0)
            {
                output.Add(buffer[0]);
                buffer[0] = null!;
            }
        }
        catch (Exception ex)
        {
            GuardLog.Warn($"[device] 枚举 DirectShow 设备失败：{ex.Message}");
        }
        finally
        {
            if (enumMoniker != null) Marshal.ReleaseComObject(enumMoniker);
            if (devEnumObj != null) Marshal.ReleaseComObject(devEnumObj);
        }
    }

    private static string NormalizePath(string path)
    {
        var p = path.Trim();
        const string devicePnp = "@device:pnp:";
        if (p.StartsWith(devicePnp, StringComparison.OrdinalIgnoreCase)) p = p[devicePnp.Length..];
        if (p.EndsWith("#", StringComparison.Ordinal)) p = p[..^1];
        return p;
    }

    // ================= 启停设备 =================

    /// <summary>启用 / 禁用摄像头设备（禁用需要管理员权限）。</summary>
    public static (bool Ok, string Message) SetEnabled(string instanceId, bool enable)
    {
        var hDevInfo = IntPtr.Zero;
        try
        {
            var classGuid = DevInterfaceVideoCamera;
            hDevInfo = SetupDiGetClassDevsW(ref classGuid, IntPtr.Zero, IntPtr.Zero,
                DigcfPresent | DigcfDeviceInterface);
            if (hDevInfo == IntPtr.Zero || hDevInfo == new IntPtr(-1))
                return (false, "无法打开设备信息集");

            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(hDevInfo, i, ref data); i++)
            {
                var sb = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceIdW(hDevInfo, ref data, sb, 512, out _)) continue;
                if (!string.Equals(sb.ToString(), instanceId, StringComparison.OrdinalIgnoreCase)) continue;

                var prm = new SP_PROPCHANGE_PARAMS
                {
                    ClassInstallHeader = new SP_CLASSINSTALL_HEADER
                    {
                        cbSize = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                        InstallFunction = DifPropertyChange
                    },
                    StateChange = enable ? DicsEnable : DicsDisable,
                    Scope = DicsFlagGlobal,
                    HwProfile = 0
                };

                if (!SetupDiSetClassInstallParamsW(hDevInfo, ref data, ref prm,
                        (uint)Marshal.SizeOf<SP_PROPCHANGE_PARAMS>()))
                {
                    var err = Marshal.GetLastWin32Error();
                    return (false, $"设置安装参数失败（错误 {err}）");
                }

                if (!SetupDiCallClassInstaller(DifPropertyChange, hDevInfo, ref data))
                {
                    var err = Marshal.GetLastWin32Error();
                    var hint = err == 5 ? "，需要以管理员身份运行 ClassIsland" : "";
                    return (false, $"调用安装程序失败（错误 {err}{hint}）");
                }

                return (true, enable ? "摄像头已恢复" : "摄像头已关闭");
            }

            return (false, "设备不存在或已被拔出");
        }
        catch (Exception ex)
        {
            GuardLog.Error("[device] 启停设备异常", ex);
            return (false, ex.Message);
        }
        finally
        {
            if (hDevInfo != IntPtr.Zero && hDevInfo != new IntPtr(-1)) SetupDiDestroyDeviceInfoList(hDevInfo);
        }
    }

    // ================= 互操作声明 =================

    [ComImport, Guid("56A86895-0AD0-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBaseFilter
    {
    }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator([In] ref Guid pType, out IEnumMoniker? ppEnumMoniker, int dwFlags);
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint pulLen, ref Guid interfaceClassGuid,
        string? pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClassGuid, string? pDeviceID,
        [Out] char[] buffer, uint bufferLen, uint ulFlags);

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint DifPropertyChange = 0x00000012;
    private const uint DicsEnable = 0x00000001;
    private const uint DicsDisable = 0x00000002;
    private const uint DicsFlagGlobal = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        StringBuilder deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParamsW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        ref SP_PROPCHANGE_PARAMS classInstallParams, uint classInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
