using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PcEnergyMeter.App;

public sealed class PowerChart : Control
{
    private readonly Queue<double> _values = new();
    private readonly string _title;
    private readonly string _unit;
    private readonly Color _lineColor;

    public PowerChart(string title, string unit, string lineColorHex = "#4ADE8B")
    {
        _title = title;
        _unit = unit;
        _lineColor = Color.Parse(lineColorHex);
        MinHeight = 160;
    }

    public void Add(double watts)
    {
        _values.Enqueue(Math.Max(0, watts));
        while (_values.Count > 120)
        {
            _values.Dequeue();
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        var chart = new Rect(16, 52, Math.Max(10, bounds.Width - 32), Math.Max(10, bounds.Height - 72));

        // Заголовок зверху, поточне значення великим — головний показник графіка.
        DrawText(context, _title, 16, 14, 13, FontWeight.SemiBold, "#9AA3B2");

        if (_values.Count < 2)
        {
            DrawText(context, "Waiting for data…", chart.X, chart.Y + chart.Height / 2 - 8, 13, FontWeight.Normal, "#6B7280");
            return;
        }

        var values = _values.ToArray();
        var max = Math.Max(_unit == "V" ? 1.5 : 50, values.Max());

        // Горизонтальні лінії сітки (25/50/75/100 %) — даюсь око для масштабу.
        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#222934")), 1);
        for (var step = 0; step <= 4; step++)
        {
            var gy = chart.Y + chart.Height * step / 4d;
            context.DrawLine(gridPen, new Point(chart.X, gy), new Point(chart.Right, gy));
        }

        // Точки лінії.
        var points = new Point[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var x = chart.X + (double)index / Math.Max(1, values.Length - 1) * chart.Width;
            var y = chart.Bottom - values[index] / max * chart.Height;
            points[index] = new Point(x, y);
        }

        // Заливка площі під лінією: градієнт від кольору лінії до прозорого.
        var area = new StreamGeometry();
        using (var stream = area.Open())
        {
            stream.BeginFigure(new Point(points[0].X, chart.Bottom), isFilled: true);
            foreach (var point in points)
            {
                stream.LineTo(point);
            }

            stream.LineTo(new Point(points[^1].X, chart.Bottom));
            stream.EndFigure(true);
        }

        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(70, _lineColor.R, _lineColor.G, _lineColor.B), 0),
                new GradientStop(Color.FromArgb(0, _lineColor.R, _lineColor.G, _lineColor.B), 1)
            }
        };
        context.DrawGeometry(fill, null, area);

        // Сама лінія.
        var linePen = new Pen(new SolidColorBrush(_lineColor), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var line = new StreamGeometry();
        using (var stream = line.Open())
        {
            stream.BeginFigure(points[0], isFilled: false);
            for (var index = 1; index < points.Length; index++)
            {
                stream.LineTo(points[index]);
            }

            stream.EndFigure(false);
        }

        context.DrawGeometry(null, linePen, line);

        DrawText(context, $"{values[^1]:0.0} {_unit}", 16, 28, 22, FontWeight.Bold, "#EAECF2");
        DrawText(context, $"max {max:0} {_unit}", chart.Right - 96, chart.Y - 18, 11, FontWeight.Normal, "#6B7280");
    }

    private static void DrawText(
        DrawingContext context,
        string text,
        double x,
        double y,
        double size,
        FontWeight weight,
        string color)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, weight),
            size,
            new SolidColorBrush(Color.Parse(color)));
        context.DrawText(formatted, new Point(x, y));
    }
}
