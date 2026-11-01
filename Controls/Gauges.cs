using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Kor.Core;
using Kor.Infrastructure;

namespace Kor.Controls;

/// <summary>
/// Gösterge animasyonları için ortak kurallar. Widget katmanlı (saydam) bir pencere olduğu için
/// her animasyon karesi tüm pencerenin yeniden çizilip ekrana kopyalanması demek; o yüzden:
///  • Gözle fark edilmeyecek kadar küçük değişimler animasyonsuz uygulanır,
///  • Kare hızı 30 ile sınırlı,
///  • Windows'ta "Animasyon efektleri" kapalıysa hiç animasyon yapılmaz.
/// Unity karşılığı: her Update'te lerp yerine sadece hedef değişince kısa bir tween başlatmak.
/// </summary>
internal static class GaugeMotion
{
    /// <summary>0..1 ölçeğinde bu kadarlık değişim ekranda ~1 pikselin altında kalır.</summary>
    private const double MinAnimatedDelta = 0.006;

    public static void To(UIElement element, DependencyProperty property, double target, int durationMs)
    {
        var current = (double)element.GetValue(property);
        if (!element.IsVisible || !SystemParameters.ClientAreaAnimation || Math.Abs(target - current) < MinAnimatedDelta)
        {
            element.BeginAnimation(property, null);
            element.SetValue(property, target);
            return;
        }

        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = Ease };
        Timeline.SetDesiredFrameRate(anim, 30);
        element.BeginAnimation(property, anim);
    }

    private static readonly IEasingFunction Ease = Paint.Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
}

/// <summary>
/// 270°'lik halka gösterge (alt kısmı açık). Değer değişince kısa bir animasyonla ilerler.
/// Unity karşılığı: Image.fillAmount = Radial360 olan bir UI Image gibi düşünülebilir.
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    private const double StartAngle = 135;
    private const double SweepAngle = 270;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0d, OnValueChanged));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(7d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0..1</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => GaugeMotion.To((RingGauge)d, DisplayValueProperty, Math.Clamp((double)e.NewValue, 0, 1), 380);

    // Animasyon karelerinde değişmeyen parçalar önbellekte: iz halkası, kalemler, parıltı fırçası.
    private (double W, double H, double T, Brush? Track) _trackKey;
    private Geometry? _trackArc;
    private Pen? _trackPen;
    private (Brush? Stroke, double T) _penKey;
    private Pen? _glowPen, _strokePen;

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        var t = Thickness;
        var radius = size / 2 - t / 2 - 1;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        var trackKey = (ActualWidth, ActualHeight, t, TrackBrush);
        if (_trackArc is null || _trackKey != trackKey)
        {
            _trackKey = trackKey;
            _trackArc = Arc(center, radius, StartAngle, SweepAngle);
            _trackPen = RoundPen(TrackBrush, t);
        }
        dc.DrawGeometry(null, _trackPen, _trackArc);

        var value = (double)GetValue(DisplayValueProperty);
        if (value < 0.004) return;

        var stroke = Stroke;
        if (_strokePen is null || _penKey != (stroke, t))
        {
            _penKey = (stroke, t);
            // Yumuşak parıltı: geniş ve saydam bir alt çizgi
            var glow = stroke.CloneCurrentValue();
            glow.Opacity = 0.18;
            _glowPen = RoundPen(glow, t * 2.1);
            _strokePen = RoundPen(stroke, t);
        }

        var arc = Arc(center, radius, StartAngle, SweepAngle * value);
        dc.DrawGeometry(null, _glowPen, arc);
        dc.DrawGeometry(null, _strokePen, arc);
    }

    private static Pen RoundPen(Brush brush, double thickness)
        => Paint.Frozen(new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    private static StreamGeometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        var a0 = startDeg * Math.PI / 180;
        var a1 = (startDeg + sweepDeg) * Math.PI / 180;
        var p0 = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
        var p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(p0, false, false);
            ctx.ArcTo(p1, new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}

/// <summary>Yuvarlatılmış yatay doluluk çubuğu.</summary>
public sealed class BarMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(BarMeter),
        new FrameworkPropertyMetadata(0d, OnValueChanged));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(BarMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(BarMeter),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(BarMeter),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => GaugeMotion.To((BarMeter)d, DisplayValueProperty, Math.Clamp((double)e.NewValue, 0, 1), 340);

    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsNaN(Height) ? 4 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var r = h / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, w, h), r, r);

        var v = (double)GetValue(DisplayValueProperty);
        if (v <= 0.002) return;
        var fw = Math.Max(h, w * v);
        dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, fw, h), r, r);
    }
}

/// <summary>Son ~2 dakikanın küçük çizgi grafiği.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(SensorHistory), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public SensorHistory? Source { get => (SensorHistory?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <summary>Çizilecek son ölçüm sayısı (1 sn aralıkla 120 = 2 dakika).</summary>
    public int Window { get; set; } = 120;
    public int Revision { get => (int)GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var buf = Source;
        var w = ActualWidth;
        var h = ActualHeight;
        if (buf is null || buf.Count < 2 || w <= 0 || h <= 0) return;

        var n = Math.Min(buf.Count, Window);
        var offset = buf.Count - n;
        double min = double.MaxValue, max = double.MinValue;
        for (var i = 0; i < n; i++)
        {
            min = Math.Min(min, buf[offset + i]);
            max = Math.Max(max, buf[offset + i]);
        }

        // Çok düz verilerde gürültüyü büyütmemek için minimum aralık
        var minRange = Math.Max(Math.Abs(max) * 0.08, 2);
        if (max - min < minRange)
        {
            var mid = (max + min) / 2;
            min = mid - minRange / 2;
            max = mid + minRange / 2;
        }

        const double pad = 2;
        var step = w / (Window - 1);
        var x0 = w - (n - 1) * step; // sağa yaslı: yeni değerler sağdan girer
        double Y(double v) => pad + (h - pad * 2) * (1 - (v - min) / (max - min));

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            var first = new Point(x0, Y(buf[offset]));
            lc.BeginFigure(first, false, false);
            ac.BeginFigure(new Point(x0, h), true, true);
            ac.LineTo(first, false, false);
            for (var i = 1; i < n; i++)
            {
                var p = new Point(x0 + i * step, Y(buf[offset + i]));
                lc.LineTo(p, true, true);
                ac.LineTo(p, false, false);
            }
            ac.LineTo(new Point(w, h), false, false);
        }
        line.Freeze();
        area.Freeze();

        // Fırça ve kalem renk değişmedikçe aynı kalır (her saniye yeniden oluşturulmasın)
        if (_paintFor != Stroke || _fill is null)
        {
            _paintFor = Stroke;
            var color = Stroke is SolidColorBrush sb ? sb.Color : Colors.DeepSkyBlue;
            _fill = Paint.Frozen(new LinearGradientBrush(Color.FromArgb(0x40, color.R, color.G, color.B), Color.FromArgb(0x00, color.R, color.G, color.B), 90));
            _pen = Paint.Frozen(new Pen(Stroke, 1.4) { LineJoin = PenLineJoin.Round });
        }

        dc.DrawGeometry(_fill, null, area);
        dc.DrawGeometry(null, _pen, line);
    }

    private Brush? _paintFor;
    private Brush? _fill;
    private Pen? _pen;
}

/// <summary>
/// 24×24'lük bir ızgarada tanımlı çizgi ikonunu istenen boyutta çizer.
/// Rengi bulunduğu kontrolün Foreground'undan miras alır.
/// </summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty =
        System.Windows.Documents.TextElement.ForegroundProperty.AddOwner(typeof(Icon),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(1.5d, FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    static Icon()
    {
        // Yerel değer yerine metadata varsayılanı: stiller de boyutu değiştirebilsin.
        WidthProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(16d));
        HeightProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(16d));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Data is null) return;
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var scale = size / 24;
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Foreground, StrokeThickness / scale)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        dc.DrawGeometry(null, pen, Data);
        dc.Pop();
        dc.Pop();
    }
}
