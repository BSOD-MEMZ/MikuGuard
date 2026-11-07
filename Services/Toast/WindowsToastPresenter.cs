using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace MikuGuard.Services.Toast;

/// <summary>一个 Toast 按钮。</summary>
public sealed record ToastActionButton(string Content, string Arguments);

/// <summary>Windows Toast 的发送与撤回。</summary>
public sealed class WindowsToastPresenter
{
    private const string Tag = "camera-guard";
    private readonly List<ToastNotification> _alive = new();
    private ToastNotifier? _notifier;

    /// <summary>最近一次失败原因（给设置页诊断用）。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>能否使用 Windows 通知（注册齐全 + 能创建 notifier）。</summary>
    public bool Available { get; private set; }

    /// <summary>尝试建立 notifier，失败时记录原因。</summary>
    public bool TryInitialize()
    {
        try
        {
            _notifier = ToastNotificationManager.CreateToastNotifier(ToastPlatform.Aumid);
            Available = true;
            LastError = "";
            GuardLog.Info("[toast] ToastNotifier 可用");
            return true;
        }
        catch (Exception ex)
        {
            _notifier = null;
            Available = false;
            LastError = $"0x{ex.HResult:X8} {ex.Message}";
            GuardLog.Warn($"[toast] 无法创建 ToastNotifier：{LastError}（将回退到插件内弹窗）");
            return false;
        }
    }

    public bool TryShow(string title, string body, IReadOnlyList<ToastActionButton> buttons)
    {
        if (_notifier == null && !TryInitialize()) return false;

        try
        {
            var actions = string.Concat(buttons.Select(b =>
                $"<action content=\"{SecurityElement.Escape(b.Content)}\" arguments=\"{SecurityElement.Escape(b.Arguments)}\" activationType=\"foreground\"/>"));

            var xml = $"""
                       <toast launch="action=none" activationType="foreground">
                         <visual>
                           <binding template="ToastGeneric">
                             <text>{SecurityElement.Escape(title)}</text>
                             <text>{SecurityElement.Escape(body)}</text>
                           </binding>
                         </visual>
                         {(actions.Length > 0 ? $"<actions>{actions}</actions>" : "")}
                       </toast>
                       """;

            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var toast = new ToastNotification(doc)
            {
                Tag = Tag,
                Group = ToastPlatform.Aumid
            };
            toast.Failed += (_, e) => GuardLog.Warn($"[toast] 发送失败 0x{e.ErrorCode:X8}");
            toast.Dismissed += (_, e) => GuardLog.Info($"[toast] 已消失：{e.Reason}");

            lock (_alive)
            {
                if (_alive.Count > 8) _alive.RemoveAt(0);
                _alive.Add(toast);
            }

            _notifier!.Show(toast);
            LastError = "";
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"0x{ex.HResult:X8} {ex.Message}";
            GuardLog.Warn($"[toast] 显示失败：{LastError}");
            return false;
        }
    }

    /// <summary>撤回当前这一组提示。</summary>
    public void Dismiss()
    {
        bool hadToast;
        lock (_alive)
        {
            hadToast = _alive.Count > 0;
            _alive.Clear();
        }

        if (!hadToast) return; // 没有提示在显示时不要惊动系统（历史里没有条目会抛异常）

        try
        {
            ToastNotificationManager.History.Remove(Tag, ToastPlatform.Aumid);
        }
        catch (Exception ex)
        {
            GuardLog.Info($"[toast] 撤回时系统返回：0x{ex.HResult:X8} {ex.Message}");
        }
    }
}
