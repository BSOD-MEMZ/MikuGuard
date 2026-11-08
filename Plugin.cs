using System.IO;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared;
using ClassIsland.Shared.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MikuGuard.Automation;
using MikuGuard.Models;
using MikuGuard.Services;
using MikuGuard.Views.Components;
using MikuGuard.Views.SettingsPages;

namespace MikuGuard;

/// <summary>插件入口。</summary>
[PluginEntrance]
public class Plugin : PluginBase
{
    /// <summary>插件设置（全局，所有组件实例共享）。</summary>
    public PluginSettings Settings { get; private set; } = new();

    private string _configPath = "";

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        GuardLog.Initialize(PluginConfigFolder);

        _configPath = Path.Combine(PluginConfigFolder, "Settings.json");
        Settings = ConfigureFileHelper.LoadConfig<PluginSettings>(_configPath);

        Settings.Monitor ??= new MonitorSettings();
        Settings.Filter ??= new FilterSettings();
        Settings.Alert ??= new AlertSettings();
        Settings.Filter.Entries ??= new System.Collections.ObjectModel.ObservableCollection<AppFilterEntry>();

        HookSave();

        // 全局设置注册为单例：设置页、服务、组件都拿同一份
        services.AddSingleton(Settings);
        services.AddSingleton<CameraGuardService>();

        // 主界面组件（带自己的显示设置面板）
        services.AddComponent<CameraIndicatorComponent, CameraIndicatorSettingsControl>();

        // 设置窗口页面
        services.AddSettingsPage<CameraGuardSettingsPage>();

        // 自动化行动（启用/停用守护、开关提醒、切提醒方式、即时操作）
        MikuGuardAutomation.Register(services);

        AppBase.Current.AppStarted += OnAppStarted;

        GuardLog.Info("插件初始化完成");
    }

    private void OnAppStarted(object? sender, EventArgs e)
    {
        try
        {
            var service = IAppHost.TryGetService<CameraGuardService>();
            var icon = MikuGuardPaths.Resolve("icon.png");
            service?.Start(File.Exists(icon) ? icon : null);
        }
        catch (Exception ex)
        {
            GuardLog.Error("启动守护服务失败", ex);
        }
    }

    // ================= 自动保存 =================

    private void HookSave()
    {
        Settings.PropertyChanged += (_, _) => Save();
        Settings.Monitor.PropertyChanged += (_, _) => Save();
        Settings.Alert.PropertyChanged += (_, _) => Save();
        Settings.Filter.PropertyChanged += (_, _) => Save();
        Settings.Filter.Entries.CollectionChanged += (_, _) =>
        {
            HookEntries();
            Save();
        };

        HookEntries();
    }

    private void HookEntries()
    {
        foreach (var entry in Settings.Filter.Entries)
        {
            entry.PropertyChanged -= OnEntryChanged;
            entry.PropertyChanged += OnEntryChanged;
        }
    }

    private void OnEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Save();

    private void Save()
    {
        try
        {
            ConfigureFileHelper.SaveConfig(_configPath, Settings);
        }
        catch (Exception ex)
        {
            GuardLog.Warn($"保存设置失败：{ex.Message}");
        }
    }
}
