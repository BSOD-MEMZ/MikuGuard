using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Core.Models.Automation;
using Microsoft.Extensions.DependencyInjection;
using MikuGuard.Models;

namespace MikuGuard.Automation;

/// <summary>
/// 把守护能力注册成 ClassIsland 自动化行动，并在「添加行动」菜单里挂一棵分组树，
/// 这样在课表里就能做「上课前关提醒 / 下课后自动恢复」这种联动。
/// </summary>
public static class MikuGuardAutomation
{
    private const string RootGroupName = "MikuGuard 摄像头守护";
    private const string RootGroupIcon = "\uE392";   // camera

    public static void Register(IServiceCollection services)
    {
        services.AddAction<SwitchGuardAction, SwitchGuardActionSettingsControl>();
        services.AddAction<SwitchAlertAction, SwitchAlertActionSettingsControl>();
        services.AddAction<SetPresentationAction, SetPresentationActionSettingsControl>();
        services.AddAction<UtilityActionAction, UtilityActionSettingsControl>();

        BuildMenuTree();
    }

    private static void BuildMenuTree()
    {
        var root = GetOrCreateGroup(IActionService.ActionMenuTree, RootGroupName, RootGroupIcon);
        if (root.Contains("摄像头守护")) return;   // 已经建过（插件重载）就别重复加

        // 守护开关
        root.Add(new ActionMenuTreeGroup("摄像头守护", "\uEF4F",
            Item<SwitchGuardActionSettings>(SwitchGuardAction.Id, "启用守护", "\uE426", s => s.Enabled = true),
            Item<SwitchGuardActionSettings>(SwitchGuardAction.Id, "停用守护", "\uE39C", s => s.Enabled = false)));

        // 提醒开关
        root.Add(new ActionMenuTreeGroup("提醒", "\uE025",
            Item<SwitchAlertActionSettings>(SwitchAlertAction.Id, "打开提醒", "\uE426", s => s.Enabled = true),
            Item<SwitchAlertActionSettings>(SwitchAlertAction.Id, "关闭提醒", "\uE671", s => s.Enabled = false)));

        // 提醒方式（含「关掉 Toast」）
        root.Add(new ActionMenuTreeGroup("提醒方式", "\uF483",
            Item<SetPresentationActionSettings>(SetPresentationAction.Id, "自动（推荐）", "\uE426",
                s => s.Presentation = AlertPresentation.Auto),
            Item<SetPresentationActionSettings>(SetPresentationAction.Id, "仅 Windows 通知", "\uF483",
                s => s.Presentation = AlertPresentation.WindowsToast),
            Item<SetPresentationActionSettings>(SetPresentationAction.Id, "仅插件内弹窗（不发 Toast）", "\uE8D1",
                s => s.Presentation = AlertPresentation.InApp)));

        // 即时操作
        root.Add(new ActionMenuTreeGroup("操作", "\uEE21",
            Item<UtilityActionSettings>(UtilityActionAction.Id, "重启监控", "\uEE21",
                s => s.Action = GuardUtilityAction.RestartMonitor),
            Item<UtilityActionSettings>(UtilityActionAction.Id, "发送测试提醒", "\uE025",
                s => s.Action = GuardUtilityAction.SendTestAlert),
            Item<UtilityActionSettings>(UtilityActionAction.Id, "重新注册通知身份", "\uE8D1",
                s => s.Action = GuardUtilityAction.ReRegisterToast)));
    }

    private static ActionMenuTreeItem<T> Item<T>(string actionId, string name, string icon, Action<T> configure)
        where T : new() => new(actionId, name, icon, configure);

    /// <summary>取「添加行动」菜单里的分组，没有就建。</summary>
    private static ActionMenuTreeNodeCollection GetOrCreateGroup(ActionMenuTreeNodeCollection parent,
        string name, string icon)
    {
        if (parent.Contains(name))
        {
            try
            {
                return parent[name];
            }
            catch (ArgumentException)
            {
                // 同名节点不是分组，下面新建一个。
            }
        }

        var created = new ActionMenuTreeGroup(name, icon);
        parent.Add(created);
        return created.Children;
    }
}
