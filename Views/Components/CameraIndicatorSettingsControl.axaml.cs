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
        RefreshBrushes();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Settings != null) Settings.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => RefreshBrushes();

    private void RefreshBrushes()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DotBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RingBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlyphPreview)));
    }

    public IBrush DotBrush => ToBrush(Settings?.DotColor, Color.Parse("#FF43A047"));

    public IBrush RingBrush => ToBrush(Settings?.RingColor, Colors.Transparent);

    public IBrush IconBrush => ToBrush(Settings?.IconColor, Colors.White);

    public string GlyphPreview => Settings?.IconGlyph ?? "";

    private static IBrush ToBrush(string? text, Color fallback)
    {
        if (!string.IsNullOrWhiteSpace(text) && Color.TryParse(text.Trim(), out var color))
            return new SolidColorBrush(color);
        return new SolidColorBrush(fallback);
    }

    private void OnPresetDotColor(object? sender, RoutedEventArgs e)
    {
        if (Settings == null || sender is not Button { Tag: string hex }) return;
        Settings.DotColor = hex;
    }

    private void OnPresetGlyph(object? sender, RoutedEventArgs e)
    {
        if (Settings == null || sender is not Button button) return;
        Settings.IconGlyph = button.Tag as string ?? "";
    }
}
