using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MikuGuard.Services;

/// <summary>
/// 判断「选定的那台摄像头」此刻是不是真的被占用。
///
/// 系统访问记录是能力级的，不区分设备；MediaCapture 走帧服务器，别人在用的时候
/// 照样能初始化成功，所以那两个都判定不了。这里直接走 DirectShow 打开选定设备并起流：
/// 起得来 = 它空闲（占用的是别的摄像头），起不来 = 被占用的就是它。
///
/// 代价是探测瞬间会真的打开一次摄像头（LED 可能闪一下），所以**只在系统记录显示
/// 有摄像头正在被使用时**才调用（摄像头全空闲时根本不探）。
/// </summary>
public static class CameraDeviceBusyProbe
{
    /// <summary>探测超时（毫秒）：DirectShow 建图卡住时不能拖死轮询线程。</summary>
    private const int ProbeTimeoutMs = 3000;

    private static readonly Guid ClsidSystemDeviceEnum = new("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
    private static readonly Guid ClsidVideoInputDeviceCategory = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");
    private static readonly Guid PinCategoryCapture = new("FB6C4281-0353-11D1-905F-0000C0CC16BA");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");

    /// <summary>
    /// 探测选定设备。
    /// </summary>
    /// <returns>true=被占用；false=空闲（占用的是别的摄像头）；null=判定不出来。</returns>
    public static bool? IsBusy(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;

        bool? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = Probe(deviceId);
            }
            catch (Exception ex)
            {
                GuardLog.Warn($"[probe] 探测异常：{ex.Message}");
                result = null;
            }
        })
        {
            IsBackground = true,
            Name = "MikuGuard-CameraProbe"
        };

        // DirectShow 的 COM 对象放在 STA 线程上最稳（宿主 UI 线程不能碰，会卡界面）
        try { thread.SetApartmentState(ApartmentState.STA); }
        catch { /* 平台不支持就算了，MTA 也能跑 */ }

        thread.Start();
        if (!thread.Join(ProbeTimeoutMs))
        {
            GuardLog.Warn("[probe] 探测超时，按「被占用」处理");
            return null;
        }

        return result;
    }

    private static bool? Probe(string deviceId)
    {
        var target = Normalize(deviceId);

        var enumType = Type.GetTypeFromCLSID(ClsidSystemDeviceEnum);
        if (enumType == null) return null;

        var devEnum = (ICreateDevEnum?)Activator.CreateInstance(enumType);
        if (devEnum == null) return null;

        var category = ClsidVideoInputDeviceCategory;
        var hr = devEnum.CreateClassEnumerator(ref category, out var monikerEnum, 0);
        if (hr == 1 || monikerEnum == null)
        {
            // S_FALSE：这个类别里一台设备都没有
            GuardLog.Warn("[probe] 系统里没有可用的摄像头");
            return null;
        }

        if (hr != 0)
        {
            GuardLog.Warn($"[probe] 取不到视频输入设备枚举器 0x{hr:X8}");
            return null;
        }

        IMoniker? moniker = null;
        var buffer = new IMoniker[1];
        while (monikerEnum.Next(1, buffer, IntPtr.Zero) == 0)
        {
            var candidate = buffer[0];
            if (candidate == null) break;
            if (Matches(candidate, target))
            {
                moniker = candidate;
                break;
            }
        }

        if (moniker == null)
        {
            // 选定的设备不在 DirectShow 列表里（被禁用 / 被拔了）：判定不出来，按占用来
            GuardLog.Warn($"[probe] DirectShow 里找不到选定设备：{deviceId}");
            return null;
        }

        IBaseFilter? capture = null;
        IGraphBuilder? graph = null;
        IBaseFilter? nullRenderer = null;
        ICaptureGraphBuilder2? builder = null;
        IMediaControl? control = null;

        try
        {
            var iid = typeof(IBaseFilter).GUID;
            object bound;
            try
            {
                moniker.BindToObject(null, null, ref iid, out bound);
            }
            catch (Exception ex)
            {
                GuardLog.Info($"[probe] 设备绑定失败（按占用处理）：{ex.Message}");
                return true;
            }

            capture = bound as IBaseFilter;
            if (capture == null) return null;

            graph = (IGraphBuilder)new FilterGraph();
            if (graph.AddFilter(capture, "MikuGuard-Capture") < 0)
            {
                GuardLog.Info("[probe] 把采集过滤器加进图里失败（按占用处理）");
                return true;
            }

            builder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
            if (builder.SetFiltergraph(graph) < 0)
            {
                GuardLog.Warn("[probe] 建图失败，判定不出来");
                return null;
            }

            nullRenderer = (IBaseFilter)new NullRenderer();
            graph.AddFilter(nullRenderer, "MikuGuard-Null");

            var categoryPin = PinCategoryCapture;
            var type = MediaTypeVideo;
            var streamHr = builder.RenderStream(ref categoryPin, ref type, capture, null, nullRenderer);
            if (streamHr < 0)
            {
                GuardLog.Info($"[probe] 连采集输出失败 0x{streamHr:X8}（按占用处理）");
                return true;
            }

            control = (IMediaControl)graph;
            streamHr = control.Run();
            if (streamHr < 0)
            {
                GuardLog.Info($"[probe] 起流失败 0x{streamHr:X8}（按占用处理）");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            GuardLog.Warn($"[probe] 探测失败（按占用处理）：{ex.Message}");
            return null;
        }
        finally
        {
            // 立刻停流并拆图，别把选定的摄像头占住
            try { control?.Stop(); } catch { /* 忽略 */ }
            try { if (graph != null && capture != null) graph.RemoveFilter(capture); } catch { /* 忽略 */ }
            try { if (graph != null && nullRenderer != null) graph.RemoveFilter(nullRenderer); } catch { /* 忽略 */ }

            Release(control);
            Release(nullRenderer);
            Release(capture);
            Release(builder);
            Release(graph);
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject == null) return;
        try
        {
            if (Marshal.IsComObject(comObject)) Marshal.FinalReleaseComObject(comObject);
        }
        catch
        {
            // 已经释放过就算了
        }
    }

    private static bool Matches(IMoniker moniker, string targetId)
    {
        string display;
        try
        {
            moniker.GetDisplayName(null, null, out display);
        }
        catch
        {
            return false;
        }

        var a = Normalize(display ?? "");
        if (a.Length == 0) return false;
        if (string.Equals(a, targetId, StringComparison.OrdinalIgnoreCase)) return true;

        // 显示名和 cfgmgr32 给的接口路径写法可能略有差异，退一步比设备实例 ID
        var ia = CameraDeviceService.ToInstanceId(a);
        var ib = CameraDeviceService.ToInstanceId(targetId);
        return ia.Length > 0 && string.Equals(ia, ib, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string id)
    {
        var s = (id ?? "").Trim();
        const string pnpPrefix = "@device:pnp:";
        if (s.StartsWith(pnpPrefix, StringComparison.OrdinalIgnoreCase)) s = s[pnpPrefix.Length..];
        return s;
    }

    // ================= COM 声明 =================

    [ComImport, Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86")]
    private class SystemDeviceEnum
    {
    }

    [ComImport, Guid("E436EBB3-524F-11CE-9F53-0020AF0BA770")]
    private class FilterGraph
    {
    }

    [ComImport, Guid("BF87B6E1-8C27-11D0-B3F0-00AA003761C5")]
    private class CaptureGraphBuilder2
    {
    }

    [ComImport, Guid("C1F400A4-3F08-11D3-9F0B-006008039E37")]
    private class NullRenderer
    {
    }

    [ComImport, Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig] int CreateClassEnumerator(ref Guid pType, out IEnumMoniker ppEnumMoniker, int dwFlags);
    }

    /// <summary>只声明要用的前两个方法（AddFilter / RemoveFilter）。</summary>
    [ComImport, Guid("56A868A9-0AD4-11CE-B03A-0020AF0BA770"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphBuilder
    {
        [PreserveSig] int AddFilter(IBaseFilter pFilter, [MarshalAs(UnmanagedType.LPWStr)] string pName);

        [PreserveSig] int RemoveFilter(IBaseFilter pFilter);
    }

    /// <summary>不直接调用它的方法，只当参数传（接口指针转换）。</summary>
    [ComImport, Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBaseFilter
    {
        [PreserveSig] int GetClassID(out Guid pClassID);
    }

    [ComImport, Guid("56A868B1-0AD4-11CE-B03A-0020AF0BA770"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMediaControl
    {
        [PreserveSig] int Stop();

        [PreserveSig] int Pause();

        [PreserveSig] int Run();
    }

    [ComImport, Guid("93E5A4E0-2D50-11D2-ABFA-00A0C9C6E38D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureGraphBuilder2
    {
        [PreserveSig] int SetFiltergraph(IGraphBuilder pfg);

        [PreserveSig] int GetFiltergraph(out IGraphBuilder ppfg);

        [PreserveSig] int SetOutputFileName(ref Guid pType, [MarshalAs(UnmanagedType.LPWStr)] string lpstrFile,
            out IBaseFilter ppf, out IntPtr ppSink);

        [PreserveSig] int FindInterface(ref Guid pCategory, ref Guid pType, IBaseFilter pf, ref Guid riid,
            out IntPtr ppint);

        [PreserveSig] int RenderStream(ref Guid pCategory, ref Guid pType,
            [MarshalAs(UnmanagedType.IUnknown)] object pSource, IBaseFilter? pfCompressor, IBaseFilter? pfRenderer);
    }
}
