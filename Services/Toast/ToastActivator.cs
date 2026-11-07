using System.Runtime.InteropServices;

namespace MikuGuard.Services.Toast;

/// <summary>
/// Toast 按钮的 COM 回调接口（Windows.UI.Notifications.INotificationActivationCallback）。
/// 声明不加 ComImport —— 这个接口是我们实现、由系统调用的。
/// </summary>
[ComVisible(true)]
[Guid("53E31837-6600-4A81-9395-75CFFE746F94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INotificationActivationCallback
{
    [PreserveSig]
    int Activate(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
        IntPtr data,
        uint count);
}

[ComVisible(true)]
[Guid("00000001-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

    [PreserveSig]
    int LockServer(bool fLock);
}

/// <summary>处理 Toast 按钮点击的激活器。系统按 CLSID 找到宿主进程里的这个类工厂。</summary>
public static class ToastActivator
{
    private const int ClsctxLocalServer = 0x4;
    private const uint RegclsMultiUse = 0x1;
    private const int SOk = 0;

    private static Action<string>? _onInvoked;
    private static object? _factoryRef;
    private static uint _cookie;
    private static bool _started;
    private static readonly object Gate = new();

    /// <summary>注册 COM 类工厂；同一进程只需注册一次。</summary>
    public static void Register(Action<string> onInvoked)
    {
        _onInvoked = onInvoked;

        lock (Gate)
        {
            if (_started) return;
            _started = true;
        }

        var thread = new Thread(ThreadProc)
        {
            IsBackground = true,
            Name = "MikuGuard.ToastActivator"
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private static void ThreadProc()
    {
        try
        {
            var hrInit = CoInitializeEx(IntPtr.Zero, 0 /*COINIT_MULTITHREADED*/);
            if (hrInit < 0 && hrInit != unchecked((int)0x80010106)) // RPC_E_CHANGED_MODE 也无所谓
            {
                GuardLog.Warn($"[toast] CoInitializeEx 失败 0x{hrInit:X8}");
                return;
            }

            _factoryRef = new ToastClassFactory(() => new NotificationActivationCallback());
            var factoryPtr = Marshal.GetComInterfaceForObject(_factoryRef, typeof(IClassFactory));
            var clsid = ToastPlatform.ActivatorClsid;
            var hr = CoRegisterClassObject(ref clsid, factoryPtr, ClsctxLocalServer, RegclsMultiUse, out _cookie);
            if (hr != SOk)
            {
                GuardLog.Warn($"[toast] CoRegisterClassObject 失败 0x{hr:X8}（Toast 按钮可能无响应）");
                return;
            }

            GuardLog.Info("[toast] 激活器已注册，等待按钮回调");
            Thread.Sleep(Timeout.Infinite);
        }
        catch (Exception ex)
        {
            GuardLog.Error("[toast] 激活器线程异常", ex);
        }
    }

    internal static void Dispatch(string args)
    {
        GuardLog.Info($"[toast] 收到按钮回调：{args}");
        try
        {
            _onInvoked?.Invoke(args);
        }
        catch (Exception ex)
        {
            GuardLog.Error("[toast] 处理按钮回调异常", ex);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(ref Guid rclsid, IntPtr pUnk,
        int dwClsContext, uint flags, out uint lpdwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);
}

internal sealed class NotificationActivationCallback : INotificationActivationCallback
{
    public int Activate(string appUserModelId, string invokedArgs, IntPtr data, uint count)
    {
        // 回调线程不是 UI 线程，交给线程池避免阻塞 COM
        var args = invokedArgs ?? "";
        ThreadPool.QueueUserWorkItem(_ => ToastActivator.Dispatch(args));
        return 0;
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ToastClassFactory : IClassFactory
{
    private readonly Func<object> _create;

    public ToastClassFactory(Func<object> create) => _create = create;

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;
        if (riid != typeof(INotificationActivationCallback).GUID)
            return unchecked((int)0x80004002); // E_NOINTERFACE

        var target = _create();
        ppvObject = Marshal.GetComInterfaceForObject(target, typeof(INotificationActivationCallback));
        if (ppvObject == IntPtr.Zero) return unchecked((int)0x80004005); // E_FAIL
        GC.KeepAlive(target);
        return 0;
    }

    public int LockServer(bool fLock) => 0;
}
