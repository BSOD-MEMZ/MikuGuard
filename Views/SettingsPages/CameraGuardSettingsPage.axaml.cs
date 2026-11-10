using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using MikuGuard.Models;
using MikuGuard.Services;
using MikuGuard.ViewModels;

namespace MikuGuard.Views.SettingsPages;

[SettingsPageInfo("mikuguard.camera-guard", "MikuGuard", "\uE392", "\uE392")]
public partial class CameraGuardSettingsPage : SettingsPageBase
{
    public CameraGuardSettingsViewModel ViewModel { get; }

    public CameraGuardSettingsPage()
    {
        var settings = IAppHost.TryGetService<PluginSettings>() ?? new PluginSettings();
        ViewModel = new CameraGuardSettingsViewModel(settings);

        InitializeComponent();
        DataContext = ViewModel;
    }

    private void OnRemoveEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AppFilterEntry entry })
            ViewModel.RemoveEntryCommand.Execute(entry);
    }

    private void OnAddDetectedClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AppIdentity app })
            ViewModel.AddDetectedAppCommand.Execute(app);
    }

    private async void OnBrowseImageClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickImageAsync("选择提醒配图");
        if (!string.IsNullOrEmpty(path)) ViewModel.Settings.Alert.ImagePath = path;
    }

    private void OnResetImageClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.Settings.Alert.ImagePath = @"Assets\monitoring.jpg";
    }

    private async void OnBrowseClosedImageClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickImageAsync("选择「摄像头已关闭」配图");
        if (!string.IsNullOrEmpty(path)) ViewModel.Settings.Alert.ClosedImagePath = path;
    }

    private void OnResetClosedImageClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.Settings.Alert.ClosedImagePath = @"Assets\monitoring_off.jpg";
    }

    private async Task<string?> PickImageAsync(string title)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return null;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("图片") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp" } }
                }
            });

            return files.FirstOrDefault()?.TryGetLocalPath();
        }
        catch (Exception ex)
        {
            GuardLog.Error("[settings] 选择图片失败", ex);
            return null;
        }
    }

    private async void OnBrowseExeClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要加入名单的程序",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("可执行文件") { Patterns = new[] { "*.exe" } }
                }
            });

            var file = files.FirstOrDefault();
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            ViewModel.AddEntryInternal(Path.GetFileName(path), path);
        }
        catch (Exception ex)
        {
            GuardLog.Error("[settings] 浏览 exe 失败", ex);
        }
    }
}
