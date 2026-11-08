using System.ComponentModel;
using Avalonia;
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RippleStyleIndex)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIslandRipple)));
    }

    public IBrush IconBrush => new SolidColorBrush(Settings?.IconColor ?? Colors.White);

    public string GlyphPreview => Settings?.IconGlyph ?? "";

    /// <summary>涟漪样式下拉框索引。</summary>
    public int RippleStyleIndex
    {
        get => Settings == null ? 0 : (int)Settings.RippleStyle;
        set
        {
            if (Settings == null || !Enum.IsDefined(typeof(RippleStyle), value)) return;
            if ((int)Settings.RippleStyle == value) return;
            Settings.RippleStyle = (RippleStyle)value;
            RaiseVisualProps();
        }
    }

    /// <summary>只有岛内涟漪才用得上时长和强度。</summary>
    public bool IsIslandRipple => Settings == null || Settings.RippleStyle == RippleStyle.InIsland;
}
