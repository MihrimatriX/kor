using System.Windows.Media;
using Kor.Core;
using Kor.Infrastructure;
using LibreHardwareMonitor.Hardware;
using static Kor.Infrastructure.Paint;

namespace Kor.ViewModels;

/// <summary>
/// Tek bir sensörün UI modeli. Aynı örnek hem widget'ta hem detay penceresinde kullanılır;
/// her ölçümde yerinde güncellenir (yeniden oluşturulmaz).
/// </summary>
public sealed class SensorItem : ObservableObject
{
    private static readonly SolidColorBrush FanBrush = Frozen(0x7D, 0xD3, 0xFC);
    private static readonly SolidColorBrush LoadBrush = Frozen(0xA7, 0x8B, 0xFA);
    private static readonly SolidColorBrush ClockBrush = Frozen(0x60, 0xA5, 0xFA);
    private static readonly SolidColorBrush PowerBrush = Frozen(0xFB, 0xBF, 0x24);
    private static readonly SolidColorBrush VoltBrush = Frozen(0xF0, 0xAB, 0xFC);
    private static readonly SolidColorBrush NeutralBrush = Frozen(0x94, 0xA3, 0xB8);
    private static readonly SolidColorBrush FpsBrush = Frozen(0x34, 0xD3, 0x99);

    private readonly AppSettings _settings;
    private SensorSample _last;

    public SensorItem(SensorSample sample, HardwareSample hardware, AppSettings settings)
    {
        _settings = settings;
        _last = sample;
        Id = sample.Id;
        OriginalName = sample.Name;
        _name = settings.SensorNames.GetValueOrDefault(sample.Id) ?? sample.Name;
        Type = sample.Type;
        Index = sample.Index;
        Kind = hardware.Kind;
        HardwareId = hardware.Id;
        HardwareName = hardware.Name;
        IsControllable = sample.Controllable;
        _accent = hardware.Kind == HwKind.Game ? FpsBrush : TypeBrush(sample.Type);
    }

    public string Id { get; }
    public string OriginalName { get; }
    public SensorType Type { get; }
    public int Index { get; }
    public HwKind Kind { get; }
    public string HardwareId { get; }
    public string HardwareName { get; }
    public bool IsTemperature => Type == SensorType.Temperature;
    public bool IsControllable { get; }
    public SensorHistory History { get; } = new();
    public string TypeTitle => Formatting.TypeTitle(Type);

    private string _name;
    /// <summary>Görünen ad: kullanıcının verdiği isim, yoksa sensörün kendi adı.</summary>
    public string Name { get => _name; private set { if (Set(ref _name, value)) OnPropertyChanged(nameof(HasCustomName)); } }
    public bool HasCustomName => _name != OriginalName;

    public void ApplyName(string? custom) => Name = string.IsNullOrWhiteSpace(custom) ? OriginalName : custom;

    private bool _isHidden;
    public bool IsHidden { get => _isHidden; set => Set(ref _isHidden, value); }

    private bool _isInWidget;
    public bool IsInWidget { get => _isInWidget; set => Set(ref _isInWidget, value); }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>Kayıtlı (LibreHardwareMonitor'un tuttuğu) oturum maksimumu, ham birimde.</summary>
    public float? SessionMax => _last.Max;

    private double? _value;
    public double? Value { get => _value; private set => Set(ref _value, value); }

    private string _valueText = "—";
    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }

    private string _unitText = "";
    public string UnitText { get => _unitText; private set => Set(ref _unitText, value); }

    private string _display = "—";
    public string Display { get => _display; private set => Set(ref _display, value); }

    private string _minText = "—";
    public string MinText { get => _minText; private set => Set(ref _minText, value); }

    private string _maxText = "—";
    public string MaxText { get => _maxText; private set => Set(ref _maxText, value); }

    private Brush _accent;
    public Brush Accent { get => _accent; private set => Set(ref _accent, value); }

    private double _fraction;
    /// <summary>0..1 — halka ve çubuklar için.</summary>
    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }

    private bool _isHot;
    public bool IsHot { get => _isHot; private set => Set(ref _isHot, value); }

    private bool _isIdle;
    /// <summary>Fan hiç dönmediyse (boş fan soketi) true.</summary>
    public bool IsIdle { get => _isIdle; private set => Set(ref _isIdle, value); }

    private int _revision;
    /// <summary>Her ölçümde artar; sparkline'ı yeniden çizdirmek için.</summary>
    public int Revision { get => _revision; private set => Set(ref _revision, value); }

    /// <returns>Bu ölçümle bir dakika tamamlandıysa o dakikanın özeti.</returns>
    public MinuteValue? Apply(SensorSample sample, DateTime time)
    {
        // Değer, min ve max aynıysa metinler ve renkler de aynı: biçimlendirmeyi atla
        // (saat hızı, veri miktarı, boşta fanlar gibi sensörlerin çoğu çoğu turda değişmez).
        var changed = !_formatted || sample.Value != _last.Value || sample.Min != _last.Min || sample.Max != _last.Max || sample.Unit != _last.Unit;
        _last = sample;
        MinuteValue? minute = null;
        if (sample.Value is float v && float.IsFinite(v)) minute = History.Add(time, v);
        if (changed) Refresh();
        Revision++;
        return minute;
    }

    private bool _formatted;

    /// <summary>Ham değeri (°C, RPM, B/s...) bu sensörün biçimiyle metne çevirir.</summary>
    public string FormatValue(double raw) => _last.Unit is { } unit
        ? $"{(unit == "ms" ? raw.ToString("0.0") : raw.ToString("0"))} {unit}"
        : Formatting.Join(Formatting.Format(Type, raw, _settings.UseFahrenheit));

    /// <summary>Özel birim (FPS, ms) varsa o, yoksa sensör türünün birimi.</summary>
    public string Unit => _last.Unit ?? Formatting.Format(Type, 0, _settings.UseFahrenheit).Unit;

    /// <summary>Birim/eşik ayarı değişince metinleri ve renkleri yeniden hesaplar.</summary>
    public void Refresh()
    {
        _formatted = true;
        var s = _last;
        var f = _settings.UseFahrenheit;

        if (s.Value is float v && float.IsFinite(v))
        {
            Value = v;
            (string Value, string Unit) fmt = s.Unit is { } unit ? (unit == "ms" ? v.ToString("0.0") : v.ToString("0"), unit) : Formatting.Format(Type, v, f);
            ValueText = fmt.Value;
            UnitText = fmt.Unit;
            Display = Formatting.Join(fmt);
        }
        else
        {
            Value = null;
            ValueText = "—";
            UnitText = "";
            Display = "—";
        }

        MinText = s.Min is float mn && float.IsFinite(mn) ? FormatValue(mn) : "—";
        MaxText = s.Max is float mx && float.IsFinite(mx) ? FormatValue(mx) : "—";

        var val = Value ?? 0;
        switch (Type)
        {
            case SensorType.Temperature:
                var (warn, crit) = TempScale.Thresholds(Kind, _settings);
                Accent = Value is null ? NeutralBrush : TempScale.BrushFor(TempScale.ColorFor(val, warn, crit));
                Fraction = Value is null ? 0 : TempScale.Fraction(val, crit);
                IsHot = Value is not null && val >= warn;
                break;
            case SensorType.Fan:
                var maxSeen = Math.Max(s.Max ?? 0, val);
                Fraction = Math.Clamp(val / Math.Max(maxSeen * 1.15, 1500), 0, 1);
                IsIdle = maxSeen <= 0;
                break;
            case SensorType.Control:
            case SensorType.Load:
            case SensorType.Level:
            case SensorType.Humidity:
                Fraction = Math.Clamp(val / 100, 0, 1);
                break;
            default:
                Fraction = 0;
                break;
        }
    }

    private static SolidColorBrush TypeBrush(SensorType type) => type switch
    {
        SensorType.Fan or SensorType.Control => FanBrush,
        SensorType.Load or SensorType.Level => LoadBrush,
        SensorType.Clock or SensorType.Frequency => ClockBrush,
        SensorType.Power or SensorType.Energy => PowerBrush,
        SensorType.Voltage or SensorType.Current => VoltBrush,
        _ => NeutralBrush,
    };


    public override string ToString() => $"{HardwareName} / {Name} = {Display}";
}
