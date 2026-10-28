using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace EtsTracker.App;

public sealed record ChartPoint(string Label, double Value);

/// <summary>Простой столбчатый график: без сторонних библиотек, рисуется в OnRender.</summary>
public sealed class BarChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<ChartPoint>), typeof(BarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartPoint>? Points
    {
        get => (IReadOnlyList<ChartPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    private static readonly Typeface Font = new("Segoe UI");
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)), 1);

    protected override void OnRender(DrawingContext dc)
    {
        // Прозрачный фон, чтобы элемент занимал место и при пустых данных
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var points = Points;
        if (points == null || points.Count == 0 || ActualWidth < 50 || ActualHeight < 50) return;

        const double left = 52, bottom = 22, top = 16;
        double w = ActualWidth - left - 4, h = ActualHeight - bottom - top;
        double max = NiceMax(points.Max(p => p.Value));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Горизонтальные линии и подписи шкалы
        for (int i = 0; i <= 4; i++)
        {
            double y = top + h - h * i / 4;
            dc.DrawLine(GridPen, new Point(left, y), new Point(left + w, y));
            var t = Text(Short(max * i / 4), dpi);
            dc.DrawText(t, new Point(left - t.Width - 6, y - t.Height / 2));
        }

        double slot = w / points.Count;
        double barW = Math.Max(2, slot * 0.68);
        int labelEvery = Math.Max(1, (int)Math.Ceiling(42 / slot));

        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            double x = left + slot * i + (slot - barW) / 2;
            double bh = max > 0 ? h * Math.Max(0, p.Value) / max : 0;
            if (bh > 0)
                dc.DrawRoundedRectangle(BarBrush, null, new Rect(x, top + h - bh, barW, bh), 2, 2);

            if (p.Value > 0 && barW >= 22)
            {
                var v = Text(Short(p.Value), dpi);
                dc.DrawText(v, new Point(x + (barW - v.Width) / 2, top + h - bh - v.Height - 1));
            }

            // Подписи считаем от правого края, чтобы последний день был подписан всегда
            if ((points.Count - 1 - i) % labelEvery == 0)
            {
                var l = Text(p.Label, dpi);
                dc.DrawText(l, new Point(left + slot * i + (slot - l.Width) / 2, top + h + 4));
            }
        }
    }

    private static FormattedText Text(string s, double dpi)
        => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 11, TextBrush, dpi);

    private static string Short(double v) => v switch
    {
        >= 1_000_000 => $"{v / 1_000_000:0.#}M",
        >= 10_000 => $"{v / 1000:0}k",
        >= 1000 => $"{v / 1000:0.#}k",
        _ => v.ToString("0"),
    };

    /// <summary>Округляет максимум шкалы вверх до «красивого» числа: 1, 2, 2.5, 5 × 10ⁿ.</summary>
    private static double NiceMax(double v)
    {
        if (v <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
            if (m * mag >= v) return m * mag;
        return 10 * mag;
    }
}
