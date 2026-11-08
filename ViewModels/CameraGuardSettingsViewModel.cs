using System.Collections.ObjectModel;
using Avalonia.Threading;
using ClassIsland.Shared;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MikuGuard.Models;
using MikuGuard.Services;
using MikuGuard.Services.Alert;
using MikuGuard.Services.Toast;

namespace MikuGuard.ViewModels;

/// <summary>摄像头守护设置页的视图模型。</summary>
public partial class CameraGuardSettingsViewModel : ObservableObject
{
    private readonly CameraGuardService? _service;
    private readonly DispatcherTimer? _timer;
    private CameraPreviewWindow? _previewWindow;

    public PluginSettings Settings { get; }

    public CameraGuardService? Service => _service;

    public ObservableCollection<CameraDeviceInfo> Devices { get; } = new();

    public ObservableCollection<AppIdentity> DetectedApps { get; } = new();

    [ObservableProperty]
    private CameraDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private string _newEntryText = "";

    [ObservableProperty]
    private string _statusText = "—";

    [ObservableProperty]
    private string _toastStatus = "—";

    [ObservableProperty]
    private string _lastActionText = "";

    public CameraGuardSettingsViewModel(PluginSettings settings)
    {
        Settings = settings;
        _service = IAppHost.TryGetService<CameraGuardService>();

        ReloadDevices();
        RefreshDetected();

        Settings.Filter.Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoFilterEntries));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => RefreshDetected();
        _timer.Start();
    }

    // ================= 摄像头列表 =================

    /// <summary>匹配模式下拉框索引。</summary>
    public int ModeIndex
    {
        get => (int)Settings.Filter.Mode;
        set
        {
            if (!Enum.IsDefined(typeof(AppFilterMode), value)) return;
            Settings.Filter.Mode = (AppFilterMode)value;
            OnPropertyChanged();
        }
    }

    /// <summary>提醒方式下拉框索引。</summary>
    public int PresentationIndex
    {
        get => (int)Settings.Alert.Presentation;
        set
        {
            if (!Enum.IsDefined(typeof(AlertPresentation), value)) return;
            Settings.Alert.Presentation = (AlertPresentation)value;
            OnPropertyChanged();
        }
    }

    private static readonly CameraDeviceInfo AnyCamera = new() { Id = "", Name = "任意摄像头（默认）" };

    public void ReloadDevices()
    {
        Devices.Clear();
        Devices.Add(AnyCamera);

        var found = _service?.Devices ?? CameraDeviceService.Enumerate();
        foreach (var device in found) Devices.Add(device);

        var current = Devices.FirstOrDefault(d =>
            string.Equals(d.Id, Settings.Monitor.DeviceId, StringComparison.OrdinalIgnoreCase));
        SelectedDevice = current ?? AnyCamera;
    }

    partial void OnSelectedDeviceChanged(CameraDeviceInfo? value)
    {
        if (value == null) return;
        Settings.Monitor.DeviceId = value.Id;
        Settings.Monitor.DeviceName = value.Name;
        OnPropertyChanged(nameof(HasConcreteDevice));
    }

    /// <summary>是否选中了具体某一台摄像头（"任意摄像头"不能预览）。</summary>
    public bool HasConcreteDevice => SelectedDevice != null && !string.IsNullOrEmpty(SelectedDevice.Id);

    /// <summary>打开预览窗口看这台摄像头的画面。</summary>
    [RelayCommand]
    private void PreviewCamera()
    {
        var device = SelectedDevice;
        if (device == null || string.IsNullOrEmpty(device.Id)) return;

        if (_previewWindow == null)
        {
            var window = new CameraPreviewWindow(device.Id, device.Name);
            window.Closed += (_, _) => _previewWindow = null;
            _previewWindow = window;
        }

        if (!_previewWindow.IsVisible) _previewWindow.Show();
        else _previewWindow.Activate();

        _ = _previewWindow.StartAsync();
    }

    // ================= 状态刷新 =================

    /// <summary>「当前没有程序在使用摄像头」提示只在列表为空时显示。</summary>
    public bool HasNoDetectedApps => DetectedApps.Count == 0;

    /// <summary>名单为空时显示占位提示。</summary>
    public bool HasNoFilterEntries => Settings.Filter.Entries.Count == 0;

    private void RefreshDetected()
    {
        if (_service == null)
        {
            StatusText = "服务未启动";
            return;
        }

        StatusText = _service.StatusText;
        LastActionText = _service.LastActionResult;

        var current = _service.DetectedApps;
        var sameSet = current.Count == DetectedApps.Count &&
                      current.Select(x => x.Key).SequenceEqual(DetectedApps.Select(x => x.Key), StringComparer.OrdinalIgnoreCase);
        if (!sameSet)
        {
            DetectedApps.Clear();
            foreach (var app in current) DetectedApps.Add(app);
            OnPropertyChanged(nameof(HasNoDetectedApps));
        }

        var alert = _service.AlertService;
        ToastStatus = alert.ToastAvailable
            ? $"Windows 通知可用（{alert.RegistrationStatus}）"
            : $"Windows 通知不可用：{alert.ToastError}；将使用插件内弹窗（{alert.RegistrationStatus}）";
    }

    // ================= 名单 =================

    [RelayCommand]
    private void AddEntry()
    {
        var text = (NewEntryText ?? "").Trim();
        if (text.Length == 0) return;
        AddEntryInternal(text, text);
        NewEntryText = "";
    }

    public void AddEntryInternal(string displayName, string match)
    {
        if (Settings.Filter.Entries.Any(e => string.Equals(e.Match, match, StringComparison.OrdinalIgnoreCase)))
            return;

        Settings.Filter.Entries.Add(new AppFilterEntry { DisplayName = displayName, Match = match });
        GuardLog.Info($"[settings] 名单新增：{match}");
    }

    [RelayCommand]
    private void RemoveEntry(AppFilterEntry? entry)
    {
        if (entry == null) return;
        Settings.Filter.Entries.Remove(entry);
    }

    [RelayCommand]
    private void AddDetectedApp(AppIdentity? app)
    {
        if (app == null) return;
        AddEntryInternal(app.DisplayName, app.Key);
    }

    // ================= 诊断 =================

    [RelayCommand]
    private void RefreshDevices()
    {
        _service?.RefreshDevices();
        ReloadDevices();
        RefreshDetected();
    }

    [RelayCommand]
    private void TestToast()
    {
        if (_service == null) return;
        var alert = new GuardAlert("C:\\Windows\\System32\\notepad.exe", "记事本（测试）", false,
            SelectedDevice?.Name ?? "摄像头", SelectedDevice?.InstanceId ?? "", SelectedDevice?.Id ?? "");
        _service.AlertService.Show(alert);
    }

    [RelayCommand]
    private void ReRegisterToast()
    {
        ToastPlatform.EnsureRegistered(IconPath());
        _service?.AlertService.Initialize(IconPath());
        RefreshDetected();
    }

    [RelayCommand]
    private void UnregisterToast()
    {
        ToastPlatform.Unregister();
        RefreshDetected();
    }


    [RelayCommand]
    private void RestartMonitor()
    {
        _service?.Restart();
        RefreshDetected();
    }

    private static string? IconPath()
    {
        var candidate = MikuGuardPaths.Resolve("icon.png");
        return File.Exists(candidate) ? candidate : null;
    }
}
