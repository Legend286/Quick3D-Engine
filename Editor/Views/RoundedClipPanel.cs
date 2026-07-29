// SPDX-License-Identifier: MIT

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Engine.Editor.Views;

/// <summary>
/// Clips Avalonia and composition child visuals to one rounded rectangle.
/// </summary>
public sealed class RoundedClipPanel : Grid
{
    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<RoundedClipPanel, double>(
            nameof(Radius),
            0.0);

    public double Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size arranged = base.ArrangeOverride(finalSize);
        Clip = new RectangleGeometry(
            new Rect(arranged),
            Radius,
            Radius);
        return arranged;
    }
}
