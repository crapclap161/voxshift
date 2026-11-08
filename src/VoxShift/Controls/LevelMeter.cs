using System;
using System.Windows;
using System.Windows.Media;

namespace VoxShift.Controls;

/// <summary>
/// Lightweight horizontal level meter (dB scale, 0..1 input) drawn directly in OnRender, which is far
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

    private static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x3A)));
    private static readonly Brush Fill = Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0x2E, 0xCC, 0x71), 0.0),
            new(Color.FromRgb(0x2E, 0xCC, 0x71), 0.70),
            new(Color.FromRgb(0xF5, 0xB9, 0x42), 0.85),
            new(Color.FromRgb(0xFF, 0x5C, 0x5C), 1.0),
        },
        new Point(0, 0), new Point(1, 0))
    { MappingMode = BrushMappingMode.RelativeToBoundingBox });
    private static readonly Brush DimFill = Freeze(new SolidColorBrush(Color.FromRgb(0x55, 0x5D, 0x6B)));
    private static readonly Pen Tick = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0x14, 0x16, 0x1B)), 1));

    public LevelMeter()
    {
        Height = 8;
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
        double radius = h / 2;
        var rect = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(Track, null, rect, radius, radius);

        double level = Math.Clamp(Level, 0, 1);
        if (level > 0.001)
        {
            // Clip the full-width gradient so colours stay anchored to dB positions.
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w * level, h), radius, radius));
            dc.DrawRoundedRectangle(IsLive ? Fill : DimFill, null, rect, radius, radius);
            dc.Pop();
        }

        // Ticks every 10 dB on a 60 dB scale.
        for (int i = 1; i < 6; i++)
        {
            double x = Math.Round(w * i / 6) + 0.5;
            dc.DrawLine(Tick, new Point(x, 1), new Point(x, h - 1));
        }
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
