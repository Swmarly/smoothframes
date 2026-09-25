using System.Windows;
using System.Windows.Media;

namespace SmoothFrames.Controls;

public sealed class FrametimeGraph : FrameworkElement
{
    private const int SampleLimit = 180;
    private static readonly Pen GridPen = CreatePen(Color.FromRgb(55, 59, 65), 1);
    private static readonly Pen TargetPen = CreateDashedPen(Color.FromRgb(81, 143, 190), 1);
    private static readonly Pen TracePen = CreatePen(Color.FromRgb(117, 191, 255), 1.6);
    private static readonly Brush TraceFill = CreateBrush(Color.FromArgb(32, 66, 150, 212));
    private readonly Queue<double> _samples = new();
    private double _targetIntervalMs = 8.33;

    public double TargetIntervalMs
    {
        get => _targetIntervalMs;
        set
        {
            _targetIntervalMs = double.IsFinite(value) && value > 0 ? value : 8.33;
            InvalidateVisual();
        }
    }

    public void AddSample(double frametimeMs)
    {
        if (!double.IsFinite(frametimeMs) || frametimeMs <= 0)
            return;

        _samples.Enqueue(frametimeMs);
        while (_samples.Count > SampleLimit)
            _samples.Dequeue();

        InvalidateVisual();
    }

    public void ClearSamples()
    {
        _samples.Clear();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 1)
            return;

        var bounds = new Rect(0, 0, width, height);
        drawingContext.DrawRectangle(Brushes.Transparent, null, bounds);

        for (var i = 1; i < 4; i++)
        {
            var y = height * i / 4d;
            drawingContext.DrawLine(GridPen, new Point(0, y), new Point(width, y));
        }

        for (var i = 1; i < 10; i++)
        {
            var x = width * i / 10d;
            drawingContext.DrawLine(GridPen, new Point(x, 0), new Point(x, height));
        }

        var scaleMs = Math.Max(Math.Max(20, _targetIntervalMs * 1.5), _samples.Count == 0 ? 0 : _samples.Max() * 1.1);
        var targetY = height - Math.Min(_targetIntervalMs, scaleMs) / scaleMs * height;
        drawingContext.DrawLine(TargetPen, new Point(0, targetY), new Point(width, targetY));

        var label = new FormattedText($"{scaleMs:0.0} ms", System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(label, new Point(7, 5));

        if (_samples.Count < 2)
            return;

        var values = _samples.ToArray();
        var stepX = width / (SampleLimit - 1d);
        var startX = width - stepX * (values.Length - 1);
        var trace = new StreamGeometry();
        using (var context = trace.Open())
        {
            var firstY = YFor(values[0], scaleMs, height);
            context.BeginFigure(new Point(startX, height), true, true);
            context.LineTo(new Point(startX, firstY), true, false);
            for (var i = 1; i < values.Length; i++)
                context.LineTo(new Point(startX + i * stepX, YFor(values[i], scaleMs, height)), true, false);
            context.LineTo(new Point(width, height), true, false);
        }
        trace.Freeze();
        drawingContext.DrawGeometry(TraceFill, null, trace);

        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(new Point(startX, YFor(values[0], scaleMs, height)), false, false);
            for (var i = 1; i < values.Length; i++)
                context.LineTo(new Point(startX + i * stepX, YFor(values[i], scaleMs, height)), true, false);
        }
        line.Freeze();
        drawingContext.DrawGeometry(null, TracePen, line);
    }

    private static double YFor(double frametimeMs, double scaleMs, double height) =>
        height - Math.Clamp(frametimeMs, 0, scaleMs) / scaleMs * height;

    private static Pen CreatePen(Color color, double thickness)
    {
        var pen = new Pen(CreateBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    private static Pen CreateDashedPen(Color color, double thickness)
    {
        var pen = new Pen(CreateBrush(color), thickness) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return pen;
    }

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
