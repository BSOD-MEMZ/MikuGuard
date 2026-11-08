using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Controls;
using MikuGuard.Models;

namespace MikuGuard.Automation;

/// <summary>
/// 行动设置控件的公共部分：一行「主控件 + 说明」。
/// 设置面板本来就很窄，不做标题，直接给控件和一句说明。
/// </summary>
internal static class ActionEditor
{
    public static StackPanel Stack(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    public static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Opacity = 0.7
    };

    public static ToggleSwitch Toggle(bool value, Action<bool> changed)
    {
        var toggle = new ToggleSwitch
        {
            IsChecked = value,
            OnContent = "开",
            OffContent = "关",
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.IsCheckedChanged += (_, _) => changed(toggle.IsChecked == true);
        return toggle;
    }

    public static ComboBox Combo(string[] items, int selected, Action<int> changed)
    {
        var combo = new ComboBox { Width = 220, SelectedIndex = selected };
        foreach (var item in items) combo.Items.Add(new ComboBoxItem { Content = item });
        combo.SelectionChanged += (_, _) => changed(combo.SelectedIndex);
        return combo;
    }
}

/// <summary>「摄像头守护开关」的设置控件。</summary>
public sealed class SwitchGuardActionSettingsControl : ActionSettingsControlBase<SwitchGuardActionSettings>
{
    public SwitchGuardActionSettingsControl()
    {
        Content = ActionEditor.Stack(
            ActionEditor.Toggle(true, value =>
            {
                Settings.Enabled = value;
                ChangeActionName(value ? "启用摄像头守护" : "停用摄像头守护");
            }),
            ActionEditor.Hint("控制整个守护。启用后，程序使用摄像头时指示点会亮起；停用时什么都不做。"));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ChangeActionName(Settings.Enabled ? "启用摄像头守护" : "停用摄像头守护");
    }
}

/// <summary>「提醒开关」的设置控件。</summary>
public sealed class SwitchAlertActionSettingsControl : ActionSettingsControlBase<SwitchAlertActionSettings>
{
    public SwitchAlertActionSettingsControl()
    {
        Content = ActionEditor.Stack(
            ActionEditor.Toggle(true, value =>
            {
                Settings.Enabled = value;
                ChangeActionName(value ? "启用提醒" : "关闭提醒");
            }),
            ActionEditor.Hint("只控制弹窗/通知，指示点仍然照常工作。"));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ChangeActionName(Settings.Enabled ? "启用提醒" : "关闭提醒");
    }
}

/// <summary>「提醒方式」的设置控件。</summary>
public sealed class SetPresentationActionSettingsControl : ActionSettingsControlBase<SetPresentationActionSettings>
{
    public SetPresentationActionSettingsControl()
    {
        Content = ActionEditor.Stack(
            ActionEditor.Combo(
                new[] { "自动（推荐）", "仅 Windows 通知", "仅插件内弹窗" },
                (int)Settings.Presentation,
                index =>
                {
                    if (!Enum.IsDefined(typeof(AlertPresentation), index)) return;
                    Settings.Presentation = (AlertPresentation)index;
                    ChangeActionName(SetPresentationAction.Describe(Settings.Presentation));
                }),
            ActionEditor.Hint("想「关掉 Toast」就选「仅插件内弹窗」：不再发系统通知，只弹插件内的卡片。"));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ChangeActionName(SetPresentationAction.Describe(Settings.Presentation));
    }
}

/// <summary>「守护操作」的设置控件。</summary>
public sealed class UtilityActionSettingsControl : ActionSettingsControlBase<UtilityActionSettings>
{
    private static readonly string[] Names = { "重启监控", "发送测试提醒", "重新注册通知" };

    public UtilityActionSettingsControl()
    {
        Content = ActionEditor.Stack(
            ActionEditor.Combo(
                Names,
                (int)Settings.Action,
                index =>
                {
                    if (!Enum.IsDefined(typeof(GuardUtilityAction), index)) return;
                    Settings.Action = (GuardUtilityAction)index;
                    ChangeActionName(Names[index]);
                }),
            ActionEditor.Hint("这类操作立即生效，没有「恢复」行为。"));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ChangeActionName(Names[(int)Settings.Action]);
    }
}
