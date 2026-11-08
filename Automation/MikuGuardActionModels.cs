using MikuGuard.Models;

namespace MikuGuard.Automation;

/// <summary>「摄像头守护开关」行动的设置。</summary>
public sealed class SwitchGuardActionSettings
{
    /// <summary>true = 启用守护，false = 停用。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>「提醒开关」行动的设置。</summary>
public sealed class SwitchAlertActionSettings
{
    /// <summary>true = 打开提醒，false = 关掉提醒。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>「提醒方式」行动的设置。</summary>
public sealed class SetPresentationActionSettings
{
    /// <summary>要切换到的提醒方式。</summary>
    public AlertPresentation Presentation { get; set; } = AlertPresentation.Auto;
}

/// <summary>「守护操作」能干的事。</summary>
public enum GuardUtilityAction
{
    /// <summary>重新启动监控（改了设备或间隔后强制重来一次）。</summary>
    RestartMonitor = 0,

    /// <summary>立刻发一条测试提醒。</summary>
    SendTestAlert = 1,

    /// <summary>重新注册 Windows 通知身份（AUMID + 快捷方式）。</summary>
    ReRegisterToast = 2
}

/// <summary>「守护操作」行动的设置。</summary>
public sealed class UtilityActionSettings
{
    /// <summary>要执行的操作。</summary>
    public GuardUtilityAction Action { get; set; } = GuardUtilityAction.RestartMonitor;
}
