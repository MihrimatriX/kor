using System.Globalization;
using System.Windows.Media;
using Kor.Core;
using Kor.Infrastructure;

namespace Kor.ViewModels;

/// <summary>
/// Bir diskin SMART sağlık özeti (CrystalDiskInfo'nun "İyi / Dikkat / Kötü" mantığı, DiskInfoToolkit üzerinden).
/// SMART okunamazsa (USB kutu, RAID, yönetici izni yok) <see cref="HasSmart"/> false olur.
/// </summary>
public sealed class DiskHealthItem(string hardwareId, string name, SensorItem? temp, SensorItem? used, SensorItem? lifeSensor) : ObservableObject
{
    private static readonly Brush GoodBrush = Frozen(0x34, 0xD3, 0x99);
    private static readonly Brush CautionBrush = Frozen(0xFB, 0xBF, 0x24);
    private static readonly Brush BadBrush = Frozen(0xF4, 0x3F, 0x5E);
    private static readonly Brush UnknownBrush = Frozen(0x8E, 0xA2, 0xBD);

    private DiskHealthInfo? _info;
    private bool _initialized;

    public string HardwareId { get; } = hardwareId;
    public string Name { get; } = name;
    public SensorItem? Temp { get; } = temp;
    public SensorItem? Used { get; } = used;
    public bool HasTemp => Temp is not null;
    public bool HasUsed => Used is not null;

    private DiskHealthStatus _status;
    public DiskHealthStatus Status { get => _status; private set => Set(ref _status, value); }

    private string _statusText = "Bilinmiyor";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private Brush _statusBrush = UnknownBrush;
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }

    private bool _hasSmart;
    public bool HasSmart { get => _hasSmart; private set => Set(ref _hasSmart, value); }

    private string _typeText = "";
    public string TypeText { get => _typeText; private set => Set(ref _typeText, value); }

    private string _lifeText = "—";
    public string LifeText { get => _lifeText; private set => Set(ref _lifeText, value); }

    private double _lifeFraction;
    public double LifeFraction { get => _lifeFraction; private set => Set(ref _lifeFraction, value); }

    private bool _hasLife;
    public bool HasLife { get => _hasLife; private set => Set(ref _hasLife, value); }

    private string _powerOnText = "—";
    public string PowerOnText { get => _powerOnText; private set => Set(ref _powerOnText, value); }

    private string _powerOnDays = "";
    public string PowerOnDays { get => _powerOnDays; private set => Set(ref _powerOnDays, value); }

    private string _powerCountText = "—";
    public string PowerCountText { get => _powerCountText; private set => Set(ref _powerCountText, value); }

    private string _writtenText = "—";
    public string WrittenText { get => _writtenText; private set => Set(ref _writtenText, value); }

    private string _summary = "";
    /// <summary>Kartta tek satırlık özet ("NVMe · 1.240 saat · 12,4 TB yazıldı").</summary>
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public void Update(DiskHealthInfo? info)
    {
        if (_initialized && Equals(info, _info) && !HasLifeSensorChanged()) return;
        _initialized = true;
        _info = info;
        var c = CultureInfo.CurrentCulture;

        HasSmart = info is not null;
        Status = info?.Status ?? DiskHealthStatus.Unknown;
        (StatusText, StatusBrush) = Status switch
        {
            DiskHealthStatus.Good => ("İyi", GoodBrush),
            DiskHealthStatus.Caution => ("Dikkat", CautionBrush),
            DiskHealthStatus.Bad => ("Kötü", BadBrush),
            _ => ("Bilinmiyor", UnknownBrush),
        };
        TypeText = info?.Type ?? "";

        var life = info?.Life ?? lifeSensor?.Value;
        _lastLife = lifeSensor?.Value;
        HasLife = life is not null;
        LifeText = life is double l ? "%" + l.ToString("0", c) : "—";
        LifeFraction = life is double lf ? Math.Clamp(lf / 100, 0, 1) : 0;

        if (info?.PowerOnHours is double h)
        {
            PowerOnText = $"{h.ToString("N0", c)} saat";
            PowerOnDays = h >= 48 ? $"≈ {(h / 24).ToString("N0", c)} gün" : "";
        }
        else
        {
            PowerOnText = "—";
            PowerOnDays = "";
        }
        PowerCountText = info?.PowerOnCount is double n ? $"{n.ToString("N0", c)} kez" : "—";
        WrittenText = info?.HostWritesGb is double gb
            ? gb >= 1024 ? $"{(gb / 1024).ToString("0.0", c)} TB" : $"{gb.ToString("N0", c)} GB"
            : "—";

        var parts = new List<string>();
        if (TypeText.Length > 0) parts.Add(TypeText);
        if (info?.PowerOnHours is double ph) parts.Add($"{ph.ToString("N0", c)} saat");
        if (WrittenText != "—") parts.Add($"{WrittenText} yazıldı");
        Summary = HasSmart ? string.Join(" · ", parts) : "SMART bilgisi okunamadı";
    }

    private double? _lastLife;
    private bool HasLifeSensorChanged() => _info?.Life is null && lifeSensor?.Value != _lastLife;

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
