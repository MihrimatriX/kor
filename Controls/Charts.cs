using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Kor.Core;
using Kor.ViewModels;

namespace Kor.Controls;

/// <summary>Grafikte çizilecek tek seri.</summary>
public sealed record ChartSeries(string Name, Brush Brush, IReadOnlyList<TimedValue> Points, Func<double, string> Format);

/// <summary>
/// Zaman serisi grafiği: ızgara, eksen etiketleri, eşik çizgisi ve fareyle üzerine gelince değer balonu.
/// Binlerce noktayı piksel başına bir noktaya indirerek çizer (OnRender, tek görsel — hafif).
/// </summary>
public sealed class HistoryChart : FrameworkElement
{
    private static Brush GridBrush => ThemeManager.Brush("B.ChartGrid", Brushes.DimGray);
    private static Brush LabelBrush => ThemeManager.Brush("B.ChartLabel", Brushes.Gray);
    private static Brush TipBack => ThemeManager.Brush("B.TipBg", Brushes.Black);
    private static Brush TipBorder => ThemeManager.Brush("B.Control", Brushes.DimGray);
    private static readonly Brush ThresholdBrush = Frozen(Color.FromArgb(0x99, 0xFB, 0x92, 0x3C));
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<ChartSeries>), typeof(HistoryChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RangeProperty = DependencyProperty.Register(
        nameof(Range), typeof(TimeSpan), typeof(HistoryChart),
        new FrameworkPropertyMetadata(TimeSpan.FromHours(1), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThresholdProperty = DependencyProperty.Register(
        nameof(Threshold), typeof(double), typeof(HistoryChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartSeries>? Series { get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public TimeSpan Range { get => (TimeSpan)GetValue(RangeProperty); set => SetValue(RangeProperty, value); }
    /// <summary>Ham birimde eşik (ör. 85 °C). NaN ise çizilmez.</summary>
    public double Threshold { get => (double)GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }

    private Point? _hover;

    public HistoryChart()
    {
        Loaded += (_, _) => ThemeManager.Changed += InvalidateVisual;
        Unloaded += (_, _) => ThemeManager.Changed -= InvalidateVisual;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 60 || h < 40) return;

        // Fare olaylarını tüm alanda almak için saydam zemin
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        const double padRight = 58, padBottom = 20, padTop = 6;
        var plot = new Rect(0, padTop, w - padRight, h - padTop - padBottom);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var series = Series ?? [];
        var now = DateTime.Now;
        var from = now - Range;

        // Y aralığı
        double min = double.MaxValue, max = double.MinValue;
        foreach (var s in series)
            foreach (var p in s.Points)
                if (p.Time >= from)
                {
                    min = Math.Min(min, p.Value);
                    max = Math.Max(max, p.Value);
                }

        var hasData = min <= max;
        if (!hasData) { min = 0; max = 100; }
        // Eşik veriye yakınsa (üstte ya da altta) görünür aralığa al
        if (!double.IsNaN(Threshold) && hasData)
        {
            var reach = Math.Max((max - min) * 1.5, Math.Max(Math.Abs(max) * 0.25, 4));
            if (Threshold > max && Threshold - max <= reach) max = Threshold;
            if (Threshold < min && min - Threshold <= reach) min = Threshold;
        }
        var span = Math.Max(max - min, Math.Max(Math.Abs(max) * 0.1, 4));
        var (lo, hi, step) = NiceScale(min - span * 0.08, max + span * 0.08);

        double X(DateTime t) => plot.Left + plot.Width * ((t - from).TotalSeconds / Range.TotalSeconds);
        double Y(double v) => plot.Bottom - plot.Height * ((v - lo) / (hi - lo));

        // Izgara + Y etiketleri
        var gridPen = new Pen(GridBrush, 1);
        var fmt = series.Count > 0 ? series[0].Format : (v => v.ToString("0", CultureInfo.CurrentCulture));
        for (var v = lo; v <= hi + step * 0.01; v += step)
        {
            var y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var text = Text(fmt(v), 10.5, LabelBrush, dpi);
            dc.DrawText(text, new Point(plot.Right + 8, y - text.Height / 2));
        }

        // Zaman etiketleri
        var timeFmt = Range > TimeSpan.FromHours(2) ? "HH:mm" : Range > TimeSpan.FromMinutes(10) ? "HH:mm" : "HH:mm:ss";
        var t0 = Text(from.ToString(timeFmt, CultureInfo.CurrentCulture), 10.5, LabelBrush, dpi);
        dc.DrawText(t0, new Point(plot.Left, plot.Bottom + 4));
        var tm = Text((from + Range / 2).ToString(timeFmt, CultureInfo.CurrentCulture), 10.5, LabelBrush, dpi);
        dc.DrawText(tm, new Point(plot.Left + plot.Width / 2 - tm.Width / 2, plot.Bottom + 4));
        var t1 = Text("şimdi", 10.5, LabelBrush, dpi);
        dc.DrawText(t1, new Point(plot.Right - t1.Width, plot.Bottom + 4));

        if (!hasData)
        {
            var empty = Text("Henüz veri yok", 12, LabelBrush, dpi);
            dc.DrawText(empty, new Point(plot.Left + plot.Width / 2 - empty.Width / 2, plot.Top + plot.Height / 2 - empty.Height / 2));
            return;
        }

        // Eşik
        if (!double.IsNaN(Threshold) && Threshold >= lo && Threshold <= hi)
        {
            var y = Y(Threshold);
            var pen = new Pen(ThresholdBrush, 1) { DashStyle = new DashStyle([4, 4], 0) };
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 2, plot.Width, plot.Height + 4)));
        for (var si = 0; si < series.Count; si++)
        {
            var s = series[si];
            var pts = Downsample(s.Points, from, X, (int)plot.Width);
            if (pts.Count < 2) continue;

            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(new Point(pts[0].X, Y(pts[0].V)), false, false);
                for (var i = 1; i < pts.Count; i++) ctx.LineTo(new Point(pts[i].X, Y(pts[i].V)), true, true);
            }
            line.Freeze();

            if (series.Count == 1)
            {
                var area = new StreamGeometry();
                using (var ctx = area.Open())
                {
                    ctx.BeginFigure(new Point(pts[0].X, plot.Bottom), true, true);
                    foreach (var p in pts) ctx.LineTo(new Point(p.X, Y(p.V)), false, false);
                    ctx.LineTo(new Point(pts[^1].X, plot.Bottom), false, false);
                }
                area.Freeze();
                var c = (s.Brush as SolidColorBrush)?.Color ?? Colors.DeepSkyBlue;
                var fill = new LinearGradientBrush(Color.FromArgb(0x38, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
                fill.Freeze();
                dc.DrawGeometry(fill, null, area);
            }
            dc.DrawGeometry(null, new Pen(s.Brush, 1.7) { LineJoin = PenLineJoin.Round }, line);
        }
        dc.Pop();

        // Üzerine gelince: dikey çizgi + nokta + değer balonu
        if (_hover is { } hp && plot.Contains(hp))
        {
            var time = from + TimeSpan.FromSeconds((hp.X - plot.Left) / plot.Width * Range.TotalSeconds);
            dc.DrawLine(new Pen(ThemeManager.Brush("B.ScrollThumbHover", Brushes.Gray), 1), new Point(hp.X, plot.Top), new Point(hp.X, plot.Bottom));

            var lines = new List<(string Text, Brush Brush)> { (time.ToString(Range > TimeSpan.FromHours(2) ? "HH:mm" : "HH:mm:ss", CultureInfo.CurrentCulture), LabelBrush) };
            var textBrush = ThemeManager.Brush("B.Text", Brushes.White);
            foreach (var s in series)
            {
                var nearest = Nearest(s.Points, time);
                if (nearest is not { } n || Math.Abs((n.Time - time).TotalSeconds) > Range.TotalSeconds / 40) continue;
                var y = Y(n.Value);
                dc.DrawEllipse(s.Brush, new Pen(TipBack, 2), new Point(X(n.Time), y), 4, 4);
                lines.Add((series.Count > 1 ? $"{s.Name}  {s.Format(n.Value)}" : s.Format(n.Value), series.Count > 1 ? s.Brush : textBrush));
            }

            var texts = lines.Select(l => Text(l.Text, 12, l.Brush, dpi)).ToList();
            var bw = texts.Max(t => t.Width) + 20;
            var bh = texts.Sum(t => t.Height) + 12;
            var bx = hp.X + 12 + bw > plot.Right ? hp.X - 12 - bw : hp.X + 12;
            var by = Math.Clamp(hp.Y - bh / 2, plot.Top, plot.Bottom - bh);
            dc.DrawRoundedRectangle(TipBack, new Pen(TipBorder, 1), new Rect(bx, by, bw, bh), 7, 7);
            var ty = by + 6;
            foreach (var t in texts)
            {
                dc.DrawText(t, new Point(bx + 10, ty));
                ty += t.Height;
            }
        }
    }

    /// <summary>Piksel sütunu başına ortalama alarak nokta sayısını azaltır.</summary>
    private static List<(double X, double V)> Downsample(IReadOnlyList<TimedValue> points, DateTime from, Func<DateTime, double> x, int width)
    {
        var result = new List<(double, double)>(Math.Min(points.Count, width + 2));
        var bucket = int.MinValue;
        double sum = 0, sx = 0;
        var n = 0;
        foreach (var p in points)
        {
            if (p.Time < from) continue;
            var px = x(p.Time);
            var b = (int)px;
            if (b != bucket && n > 0)
            {
                result.Add((sx / n, sum / n));
                sum = sx = 0;
                n = 0;
            }
            bucket = b;
            sum += p.Value;
            sx += px;
            n++;
        }
        if (n > 0) result.Add((sx / n, sum / n));
        return result;
    }

    private static TimedValue? Nearest(IReadOnlyList<TimedValue> points, DateTime time)
    {
        if (points.Count == 0) return null;
        int lo = 0, hi = points.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (points[mid].Time < time) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && (time - points[lo - 1].Time).Duration() < (points[lo].Time - time).Duration()) lo--;
        return points[lo];
    }

    private static (double Lo, double Hi, double Step) NiceScale(double min, double max)
    {
        var range = max - min;
        var rough = range / 4;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var norm = rough / mag;
        var step = (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;
        var lo = Math.Floor(min / step) * step;
        var hi = Math.Ceiling(max / step) * step;
        if (lo < 0 && min >= 0) lo = 0;
        return (lo, hi, step);
    }

    private static FormattedText Text(string s, double size, Brush brush, double dpi)
        => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush, dpi);

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>
/// Fan eğrisi düzenleyici: X = sıcaklık (20–100 °C), Y = fan hızı (%0–100).
/// Noktalar fareyle sürüklenir; komşu noktaları geçemez. Anlık sıcaklık dikey çizgiyle gösterilir.
/// Unity karşılığı: AnimationCurve editörü (ama sabit 5 nokta, doğrusal).
/// </summary>
public sealed class FanCurveEditor : FrameworkElement
{
    private const double MinT = 20, MaxT = 100;
    private const double PadLeft = 40, PadBottom = 22, PadTop = 10, PadRight = 12;
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IList<CurvePoint>), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPointsChanged));

    public static readonly DependencyProperty CurrentTempProperty = DependencyProperty.Register(
        nameof(CurrentTemp), typeof(double), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)), FrameworkPropertyMetadataOptions.AffectsRender));

    public IList<CurvePoint>? Points { get => (IList<CurvePoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public double CurrentTemp { get => (double)GetValue(CurrentTempProperty); set => SetValue(CurrentTempProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    private int _drag = -1;
    private int _hoverIndex = -1;

    public FanCurveEditor()
    {
        Loaded += (_, _) => ThemeManager.Changed += InvalidateVisual;
        Unloaded += (_, _) => ThemeManager.Changed -= InvalidateVisual;
    }

    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (FanCurveEditor)d;
        if (e.OldValue is IList<CurvePoint> oldList)
        {
            foreach (var p in oldList) p.PropertyChanged -= editor.OnPointChanged;
            if (oldList is INotifyCollectionChanged oc) oc.CollectionChanged -= editor.OnCollectionChanged;
        }
        if (e.NewValue is IList<CurvePoint> newList)
        {
            foreach (var p in newList) p.PropertyChanged += editor.OnPointChanged;
            if (newList is INotifyCollectionChanged nc) nc.CollectionChanged += editor.OnCollectionChanged;
        }
    }

    private void OnPointChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (CurvePoint p in e.OldItems) p.PropertyChanged -= OnPointChanged;
        if (e.NewItems is not null) foreach (CurvePoint p in e.NewItems) p.PropertyChanged += OnPointChanged;
        InvalidateVisual();
    }

    private Rect Plot => new(PadLeft, PadTop, Math.Max(10, ActualWidth - PadLeft - PadRight), Math.Max(10, ActualHeight - PadTop - PadBottom));
    private Point ToScreen(double t, double p) => new(Plot.Left + Plot.Width * (t - MinT) / (MaxT - MinT), Plot.Bottom - Plot.Height * p / 100);

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 80 || ActualHeight < 60) return;
        var plot = Plot;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var labelBrush = ThemeManager.Brush("B.ChartLabel", Brushes.Gray);
        var gridPen = new Pen(ThemeManager.Brush("B.ChartGrid", Brushes.DimGray), 1);

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        for (var p = 0; p <= 100; p += 25)
        {
            var y = Math.Round(ToScreen(MinT, p).Y) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var text = new FormattedText("%" + p, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10.5, labelBrush, dpi);
            dc.DrawText(text, new Point(plot.Left - text.Width - 8, y - text.Height / 2));
        }
        for (var t = MinT; t <= MaxT; t += 10)
        {
            var x = Math.Round(ToScreen(t, 0).X) + 0.5;
            dc.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var text = new FormattedText($"{t:0}°", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10.5, labelBrush, dpi);
            dc.DrawText(text, new Point(x - text.Width / 2, plot.Bottom + 5));
        }

        var points = Points;
        if (points is null || points.Count == 0) return;
        var ordered = points.OrderBy(p => p.Temp).ToList();

        // Eğri (uçlarda düz devam eder)
        var curve = new StreamGeometry();
        var area = new StreamGeometry();
        using (var c = curve.Open())
        using (var a = area.Open())
        {
            var start = ToScreen(MinT, ordered[0].Percent);
            c.BeginFigure(start, false, false);
            a.BeginFigure(new Point(start.X, plot.Bottom), true, true);
            a.LineTo(start, false, false);
            foreach (var p in ordered)
            {
                var sp = ToScreen(p.Temp, p.Percent);
                c.LineTo(sp, true, true);
                a.LineTo(sp, false, false);
            }
            var end = ToScreen(MaxT, ordered[^1].Percent);
            c.LineTo(end, true, true);
            a.LineTo(end, false, false);
            a.LineTo(new Point(end.X, plot.Bottom), false, false);
        }
        var accent = (Accent as SolidColorBrush)?.Color ?? Colors.DeepSkyBlue;
        var fill = new LinearGradientBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B), Color.FromArgb(0x04, accent.R, accent.G, accent.B), 90);
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Accent, 2) { LineJoin = PenLineJoin.Round }, curve);

        // Anlık sıcaklık
        if (!double.IsNaN(CurrentTemp))
        {
            var t = Math.Clamp(CurrentTemp, MinT, MaxT);
            var pct = FanCurve.Evaluate(ordered.Select(p => ((float)p.Temp, (float)p.Percent)).ToList(), (float)CurrentTemp);
            var sp = ToScreen(t, pct);
            var dash = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0x7A, 0x45)), 1) { DashStyle = new DashStyle([3, 3], 0) };
            dc.DrawLine(dash, new Point(sp.X, plot.Top), new Point(sp.X, plot.Bottom));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x45)), null, sp, 4, 4);
            var label = new FormattedText($"{CurrentTemp:0}° → %{pct:0}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11.5,
                new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x6E)), dpi);
            var lx = sp.X + 8 + label.Width > plot.Right ? sp.X - 8 - label.Width : sp.X + 8;
            dc.DrawText(label, new Point(lx, plot.Top + 2));
        }

        // Tutamaklar
        var back = ThemeManager.Brush("B.Card", Brushes.Black);
        var tipBack = ThemeManager.Brush("B.TipBg", Brushes.Black);
        var tipText = ThemeManager.Brush("B.Text", Brushes.White);
        for (var i = 0; i < points.Count; i++)
        {
            var sp = ToScreen(points[i].Temp, points[i].Percent);
            var active = i == _drag || i == _hoverIndex;
            dc.DrawEllipse(i == _drag ? Accent : back, new Pen(Accent, 2), sp, active ? 7 : 5.5, active ? 7 : 5.5);
            if (active)
            {
                var tip = new FormattedText($"{points[i].Temp:0}° · %{points[i].Percent:0}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11.5, tipText, dpi);
                var tx = Math.Clamp(sp.X - tip.Width / 2, plot.Left, plot.Right - tip.Width);
                var ty = sp.Y - 26 < plot.Top ? sp.Y + 12 : sp.Y - 26;
                dc.DrawRoundedRectangle(tipBack, new Pen(ThemeManager.Brush("B.Line", Brushes.Gray), 1), new Rect(tx - 6, ty - 2, tip.Width + 12, tip.Height + 4), 5, 5);
                dc.DrawText(tip, new Point(tx, ty));
            }
        }
    }

    private int HitTest(Point pos)
    {
        var points = Points;
        if (points is null) return -1;
        var best = -1;
        var bestDist = 14.0;
        for (var i = 0; i < points.Count; i++)
        {
            var d = (ToScreen(points[i].Temp, points[i].Percent) - pos).Length;
            if (d < bestDist) { best = i; bestDist = d; }
        }
        return best;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _drag = HitTest(e.GetPosition(this));
        if (_drag >= 0)
        {
            CaptureMouse();
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pos = e.GetPosition(this);
        var points = Points;
        if (_drag >= 0 && points is not null)
        {
            var ordered = points.OrderBy(p => p.Temp).ToList();
            var point = points[_drag];
            var idx = ordered.IndexOf(point);
            var lowT = idx > 0 ? ordered[idx - 1].Temp + 1 : MinT;
            var highT = idx < ordered.Count - 1 ? ordered[idx + 1].Temp - 1 : MaxT;

            var plot = Plot;
            var t = MinT + (pos.X - plot.Left) / plot.Width * (MaxT - MinT);
            var p = (plot.Bottom - pos.Y) / plot.Height * 100;
            point.Temp = Math.Round(Math.Clamp(t, lowT, highT));
            point.Percent = Math.Round(Math.Clamp(p, 0, 100));
            return;
        }

        var hover = HitTest(pos);
        if (hover != _hoverIndex)
        {
            _hoverIndex = hover;
            Cursor = hover >= 0 ? Cursors.Hand : null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag < 0) return;
        _drag = -1;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_drag >= 0) return;
        _hoverIndex = -1;
        InvalidateVisual();
    }
}
