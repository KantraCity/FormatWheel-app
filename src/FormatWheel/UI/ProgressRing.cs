using System;
using System.Windows;
using System.Windows.Media;

namespace FormatWheel;

public sealed class ProgressRing : FrameworkElement
{
    private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // 0..1, через DependencyProperty, чтобы можно было анимировать
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush RingBrush { get => (Brush)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        double r = (size - Thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(TrackBrush, Thickness), c, r, r);

        double v = Math.Clamp(Value, 0, 1);
        if (v <= 0.001) return;

        var pen = new Pen(RingBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v >= 0.999)
        {
            dc.DrawEllipse(null, pen, c, r, r); // ArcSegment на полный круг не рисуется
            return;
        }

        Point At(double deg)
        {
            double rad = (deg - 90) * Math.PI / 180; // старт сверху
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }

        double sweep = v * 360;
        var figure = new PathFigure { StartPoint = At(0), IsClosed = false };
        figure.Segments.Add(new ArcSegment(At(sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        dc.DrawGeometry(null, pen, geometry);
    }

    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }
}
