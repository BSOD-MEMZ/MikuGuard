using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MikuGuard.Models;

/// <summary>程序匹配模式。</summary>
public enum AppFilterMode
{
    /// <summary>名单内程序不提醒，其它都提醒。</summary>
    Blacklist = 0,

    /// <summary>只有名单内程序会提醒。</summary>
    Whitelist = 1,

    /// <summary>不区分程序，一律提醒。</summary>
    All = 2
}

/// <summary>提醒方式。</summary>
public enum AlertPresentation
{
    /// <summary>优先 Windows 通知，失败时回退到插件内弹窗。</summary>
    Auto = 0,

    /// <summary>只用 Windows 通知（系统 Toast）。</summary>
    WindowsToast = 1,

    /// <summary>只用插件内弹窗。</summary>
    InApp = 2
}

/// <summary>程序过滤名单条目。匹配串可以是完整路径、exe 文件名或 UWP 包族名。</summary>
public partial class AppFilterEntry : ObservableObject
{
    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private string _match = "";
}

/// <summary>摄像头监控设置。</summary>
public partial class MonitorSettings : ObservableObject
{
    /// <summary>是否启用监控。</summary>
    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>轮询间隔（毫秒）。</summary>
    [ObservableProperty]
    private int _pollingIntervalMs = 1500;

    /// <summary>要监控的摄像头设备路径（空 = 任意摄像头）。</summary>
    [ObservableProperty]
    private string _deviceId = "";

    /// <summary>设备显示名（仅用于界面展示）。</summary>
    [ObservableProperty]
    private string _deviceName = "";

}

/// <summary>程序过滤设置。</summary>
public partial class FilterSettings : ObservableObject
{
    [ObservableProperty]
    private AppFilterMode _mode = AppFilterMode.Blacklist;

    [ObservableProperty]
    private ObservableCollection<AppFilterEntry> _entries = new();
}

/// <summary>警报（Toast / 弹窗）设置。</summary>
public partial class AlertSettings : ObservableObject
{
    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private AlertPresentation _presentation = AlertPresentation.Auto;

    /// <summary>同一个程序两次提醒之间的最小间隔（秒）。</summary>
    [ObservableProperty]
    private int _cooldownSeconds = 60;

    [ObservableProperty]
    private string _title = "摄像头正在被使用";

    [ObservableProperty]
    private bool _showDismissAction = true;

    [ObservableProperty]
    private bool _showDisableCameraAction = true;

    [ObservableProperty]
    private bool _showCloseAppAction = true;

    /// <summary>执行动作后，用一条通知回执告诉用户结果。</summary>
    [ObservableProperty]
    private bool _showActionResult = true;

    /// <summary>提醒里显示的图片（相对路径按插件目录解析）。留空则不显示图片。</summary>
    [ObservableProperty]
    private string _imagePath = @"Assets\monitoring.jpg";
}

/// <summary>插件根设置。</summary>
public partial class PluginSettings : ObservableObject
{
    [ObservableProperty]
    private MonitorSettings _monitor = new();

    [ObservableProperty]
    private FilterSettings _filter = new();

    [ObservableProperty]
    private AlertSettings _alert = new();
}
