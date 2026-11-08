using System.Collections.Concurrent;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using MikuGuard.Models;
using MikuGuard.Services;
using MikuGuard.Services.Alert;
using MikuGuard.Services.Toast;

namespace MikuGuard.Automation;

/// <summary>
/// 自动化行动触发前的设置快照。行动实例是瞬态的（每次触发都新建一个），
/// 所以「恢复」要用的旧值不能放在实例字段里，得按「行动组 Guid + 行动 Id」存起来。
/// </summary>
internal sealed class GuardSnapshot
{
    public bool MonitorEnabled { get; init; }

    public bool AlertEnabled { get; init; }

    public AlertPresentation Presentation { get; init; }
}

/// <summary>行动快照仓库。</summary>
internal static class GuardActionState
{
    private static readonly ConcurrentDictionary<string, GuardSnapshot> Values = new();

    private static string Key(Guid actionSetGuid, string actionId) => $"{actionSetGuid}|{actionId}";

    /// <summary>在改动设置前记下当前状态。</summary>
    public static void Save(Guid actionSetGuid, string actionId, PluginSettings settings)
    {
        Values[Key(actionSetGuid, actionId)] = new GuardSnapshot
        {
            MonitorEnabled = settings.Monitor.Enabled,
            AlertEnabled = settings.Alert.Enabled,
            Presentation = settings.Alert.Presentation
        };
    }

    /// <summary>取出并清除快照。</summary>
    public static bool TryTake(Guid actionSetGuid, string actionId, out GuardSnapshot? snapshot) =>
        Values.TryRemove(Key(actionSetGuid, actionId), out snapshot);

    /// <summary>按快照还原，找不到快照就什么都不做。</summary>
    public static void Restore(Guid actionSetGuid, string actionId)
    {
        if (!TryTake(actionSetGuid, actionId, out var snapshot) || snapshot == null) return;

        var settings = IAppHost.TryGetService<PluginSettings>();
        if (settings == null) return;

        settings.Monitor.Enabled = snapshot.MonitorEnabled;
        settings.Alert.Enabled = snapshot.AlertEnabled;
        settings.Alert.Presentation = snapshot.Presentation;
        GuardLog.Info("[automation] 已恢复触发前的守护设置");
    }
}

/// <summary>
/// 「摄像头守护开关」：打开或关掉整个守护（等同于设置页里的「启用摄像头守护」）。
/// 支持恢复——课程结束后自动把守护打开，就是靠这个 OnRevert。
/// </summary>
[ActionInfo(SwitchGuardAction.Id, "摄像头守护开关", "\uEF4F", addDefaultToMenu: false)]
public sealed class SwitchGuardAction : ActionBase<SwitchGuardActionSettings>
{
    public const string Id = "mikuguard.camera-guard.switchGuard";

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();

        var settings = IAppHost.TryGetService<PluginSettings>();
        if (settings == null) return;

        GuardActionState.Save(ActionSet.Guid, Id, settings);
        settings.Monitor.Enabled = Settings.Enabled;
        GuardLog.Info($"[automation] 摄像头守护 → {(Settings.Enabled ? "启用" : "停用")}");
    }

    protected override async Task OnRevert()
    {
        await base.OnRevert();
        GuardActionState.Restore(ActionSet.Guid, Id);
    }
}

/// <summary>
/// 「提醒开关」：只关提醒，指示点照常亮（想彻底安静就用守护开关）。
/// </summary>
[ActionInfo(SwitchAlertAction.Id, "提醒开关", "\uE025", addDefaultToMenu: false)]
public sealed class SwitchAlertAction : ActionBase<SwitchAlertActionSettings>
{
    public const string Id = "mikuguard.camera-guard.switchAlert";

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();

        var settings = IAppHost.TryGetService<PluginSettings>();
        if (settings == null) return;

        GuardActionState.Save(ActionSet.Guid, Id, settings);
        settings.Alert.Enabled = Settings.Enabled;
        GuardLog.Info($"[automation] 提醒 → {(Settings.Enabled ? "启用" : "关闭")}");
    }

    protected override async Task OnRevert()
    {
        await base.OnRevert();
        GuardActionState.Restore(ActionSet.Guid, Id);
    }
}

/// <summary>
/// 「提醒方式」：自动 / 仅 Windows 通知（Toast）/ 仅插件内弹窗。
/// 「关掉 Toast」就是把这里切到「仅插件内弹窗」。
/// </summary>
[ActionInfo(SetPresentationAction.Id, "提醒方式", "\uF483", addDefaultToMenu: false)]
public sealed class SetPresentationAction : ActionBase<SetPresentationActionSettings>
{
    public const string Id = "mikuguard.camera-guard.setPresentation";

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();

        var settings = IAppHost.TryGetService<PluginSettings>();
        if (settings == null) return;

        GuardActionState.Save(ActionSet.Guid, Id, settings);
        settings.Alert.Presentation = Settings.Presentation;
        GuardLog.Info($"[automation] 提醒方式 → {Describe(Settings.Presentation)}");
    }

    protected override async Task OnRevert()
    {
        await base.OnRevert();
        GuardActionState.Restore(ActionSet.Guid, Id);
    }

    internal static string Describe(AlertPresentation presentation) => presentation switch
    {
        AlertPresentation.WindowsToast => "仅 Windows 通知",
        AlertPresentation.InApp => "仅插件内弹窗",
        _ => "自动"
    };
}

/// <summary>
/// 「守护操作」：重启监控 / 发测试提醒 / 重注册通知。
/// 这几个都是即时动作，没有恢复行为。
/// </summary>
[ActionInfo(UtilityActionAction.Id, "守护操作", "\uEE21", addDefaultToMenu: false)]
public sealed class UtilityActionAction : ActionBase<UtilityActionSettings>
{
    public const string Id = "mikuguard.camera-guard.utility";

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();

        var service = IAppHost.TryGetService<CameraGuardService>();
        if (service == null)
        {
            GuardLog.Warn("[automation] 守护服务还没起来，操作被跳过");
            return;
        }

        switch (Settings.Action)
        {
            case GuardUtilityAction.RestartMonitor:
                await Dispatcher.UIThread.InvokeAsync(service.Restart);
                GuardLog.Info("[automation] 已重启监控");
                break;

            case GuardUtilityAction.SendTestAlert:
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var device = service.Devices.FirstOrDefault();
                    service.AlertService.Show(new GuardAlert(
                        @"C:\Windows\System32\notepad.exe",
                        "记事本（测试）",
                        false,
                        device?.Name ?? "摄像头",
                        device?.InstanceId ?? "",
                        device?.Id ?? ""));
                });
                GuardLog.Info("[automation] 已发送测试提醒");
                break;

            case GuardUtilityAction.ReRegisterToast:
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var icon = MikuGuardPaths.Resolve("icon.png");
                    ToastPlatform.EnsureRegistered(File.Exists(icon) ? icon : null);
                    service.AlertService.Initialize(File.Exists(icon) ? icon : null);
                });
                GuardLog.Info("[automation] 已重新注册 Windows 通知");
                break;
        }
    }
}
