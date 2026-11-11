using System;
using System.Windows;
using System.Windows.Media;

namespace VoxShift.Controls;

/// <summary>
/// Lightweight segmented (LED-style) horizontal level meter (dB scale, 0..1 input) drawn directly in OnRender, which is far
/// cheaper than a templated ProgressBar when updated ~30 times per second.
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.Register(
        nameof(IsLive), typeof(bool), typeof(LevelMeter),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double SegmentWidth = 3;
    private const double SegmentGap = 2;

    private static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromRgb(0x25, 0x2A, 0x36)));
    private static readonly Brush Fill = Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0x3D, 0xDC, 0x84), 0.0),
            new(Color.FromRgb(0x3D, 0xDC, 0x84), 0.68),
            new(Color.FromRgb(0xF5, 0xC0, 0x4E), 0.84),
            new(Color.FromRgb(0xFF, 0x6B, 0x6B), 1.0),
        },
        new Point(0, 0), new Point(1, 0))
    { MappingMode = BrushMappingMode.RelativeToBoundingBox });
    private static readonly Brush DimFill = Freeze(new SolidColorBrush(Color.FromRgb(0x5F, 0x68, 0x78)));

    // One opaque segment followed by a transparent gap, tiled across the meter: gives the LED-segment look with a
    // single opacity mask instead of one draw call per segment.
    private static readonly Brush SegmentMask = Freeze(new DrawingBrush(
        new GeometryDrawing(Brushes.Black, null, new RectangleGeometry(new Rect(0, 0, SegmentWidth, 1))))
    {
        TileMode = TileMode.Tile,
        Viewport = new Rect(0, 0, SegmentWidth + SegmentGap, 1),
        ViewportUnits = BrushMappingMode.Absolute,
        Viewbox = new Rect(0, 0, SegmentWidth + SegmentGap, 1),
        ViewboxUnits = BrushMappingMode.Absolute,
        Stretch = Stretch.Fill,
    });

    public LevelMeter()
    {
        Height = 8;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
    }

    /// <summary>Meter position 0..1 (already mapped to the dB scale).</summary>
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <summary>When false the bar is drawn grey (source captured but not transmitted).</summary>
    public bool IsLive
    {
        get => (bool)GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var rect = new Rect(0, 0, w, h);

        dc.PushOpacityMask(SegmentMask);
        dc.DrawRectangle(Track, null, rect);

        double level = Math.Clamp(Level, 0, 1);
        if (level > 0.001)
        {
            // Light whole segments only, and clip the full-width gradient so colours stay anchored to dB positions.
            double step = SegmentWidth + SegmentGap;
            double lit = Math.Min(w, Math.Ceiling(w * level / step) * step);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, lit, h)));
            dc.DrawRectangle(IsLive ? Fill : DimFill, null, rect);
            dc.Pop();
        }

        dc.Pop();
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
