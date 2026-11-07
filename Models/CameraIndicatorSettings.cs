using System.Text.Json.Serialization;
using Avalonia.Media;
using ClassIsland.Core.Converters;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MikuGuard.Models;

/// <summary>
/// 摄像头安全指示器组件的显示设置（跟随组件实例保存，可多维一份、各自不同颜色）。
/// </summary>
public partial class CameraIndicatorSettings : ObservableObject
{
    /// <summary>圆点颜色。</summary>
    [ObservableProperty]
    [property: JsonConverter(typeof(ColorHexJsonConverter))]
    private Color _dotColor = Color.Parse("#FF43A047");

    /// <summary>外圈描边颜色（A=0 表示不描边）。</summary>
    [ObservableProperty]
    [property: JsonConverter(typeof(ColorHexJsonConverter))]
    private Color _ringColor = Color.FromArgb(0, 255, 255, 255);

    /// <summary>图标字符（Fluent 图标字形的 Unicode 字符），留空 = 不显示图标。</summary>
    [ObservableProperty]
    private string _iconGlyph = "\uE392";

    /// <summary>图标颜色。</summary>
    [ObservableProperty]
    [property: JsonConverter(typeof(ColorHexJsonConverter))]
    private Color _iconColor = Colors.White;

    /// <summary>收缩后的圆点尺寸（px）。</summary>
    [ObservableProperty]
    private double _dotSize = 9;

    /// <summary>展开时的尺寸（px），展开时显示图标。</summary>
    [ObservableProperty]
    private double _expandedSize = 24;

    /// <summary>展开保持多久后收缩为小圆点（毫秒）。0 = 一直保持展开。</summary>
    [ObservableProperty]
    private int _expandHoldMs = 3000;

    /// <summary>摄像头持续被使用时呼吸闪烁。</summary>
    [ObservableProperty]
    private bool _pulse = true;

    /// <summary>鼠标悬停时用提示气泡显示是哪个程序在用摄像头。</summary>
    [ObservableProperty]
    private bool _showAppTooltip = true;
}
