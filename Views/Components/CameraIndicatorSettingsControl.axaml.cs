using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Controls;
using MikuGuard.Models;

namespace MikuGuard.Views.Components;

public partial class CameraIndicatorSettingsControl : ComponentBase<CameraIndicatorSettings>, INotifyPropertyChanged
{
    public new event PropertyChangedEventHandler? PropertyChanged;

    public CameraIndicatorSettingsControl()
    {
        InitializeComponent();
        DataContext = this;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Settings == null) return;

        Settings.PropertyChanged -= OnSettingsChanged;
        Settings.PropertyChanged += OnSettingsChanged;
        RaiseVisualProps();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Settings != null) Settings.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => RaiseVisualProps();

    private void RaiseVisualProps()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlyphPreview)));
    }

    public IBrush IconBrush => new SolidColorBrush(Settings?.IconColor ?? Colors.White);

    public string GlyphPreview => Settings?.IconGlyph ?? "";

    private void OnPresetDotColor(object? sender, RoutedEventArgs e)
    {
        if (Settings == null || sender is not Button { Tag: string hex }) return;
        if (Color.TryParse(hex, out var color)) Settings.DotColor = color;
    }

    private void OnPresetGlyph(object? sender, RoutedEventArgs e)
    {
        if (Settings == null || sender is not Button button) return;
        Settings.IconGlyph = button.Tag as string ?? "";
    }
}
