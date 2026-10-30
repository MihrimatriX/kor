using System.Collections.ObjectModel;
using System.Windows.Input;
using Kor.Core;
using Kor.Infrastructure;

namespace Kor.ViewModels;

/// <summary>Eğri üzerindeki sürüklenebilir nokta.</summary>
public sealed class CurvePoint(double temp, double percent) : ObservableObject
{
    private double _temp = temp;
    public double Temp { get => _temp; set => Set(ref _temp, value); }

    private double _percent = percent;
    public double Percent { get => _percent; set => Set(ref _percent, value); }
}

/// <summary>
/// "Fan kontrolü" sayfasındaki tek bir kontrol edilebilir fan (LibreHardwareMonitor Control sensörü).
/// Değişiklikler <see cref="Changed"/> ile bildirilir; MainViewModel ayarlara yazar ve sensör thread'ine plan gönderir.
/// </summary>
public sealed class FanControlItem : ObservableObject
{
    private readonly Func<string, SensorItem?> _resolve;
    private bool _suppress;

    public FanControlItem(SensorItem control, SensorItem? fan, FanCurveSettings settings, Func<string, SensorItem?> resolve, string? defaultSourceId)
    {
        Control = control;
        Fan = fan;
        _resolve = resolve;
        _mode = settings.Mode;
        _fixedPercent = settings.FixedPercent;
        _sourceId = settings.SourceId ?? defaultSourceId;
        Points = new ObservableCollection<CurvePoint>(settings.Points.Select(p => new CurvePoint(p.Temp, p.Percent)));
        foreach (var p in Points) p.PropertyChanged += (_, _) => RaiseChanged();

        SetModeCommand = new RelayCommand(p => { if (p is FanMode m) Mode = m; else if (p is string s && Enum.TryParse<FanMode>(s, out var parsed)) Mode = parsed; });
        PresetCommand = new RelayCommand(p => ApplyPreset(p as string ?? "Balanced"));
    }

    public event Action<FanControlItem>? Changed;

    public SensorItem Control { get; }
    /// <summary>Aynı donanım ve indeksteki RPM sensörü (varsa).</summary>
    public SensorItem? Fan { get; }
    public bool HasFan => Fan is not null;
    /// <summary>Fanın adı (kullanıcı yeniden adlandırdıysa o ad), yoksa kontrol sensörünün adı.</summary>
    public string Title => Fan?.Name ?? Control.Name;
    public string Subtitle => Control.HardwareName;

    public ObservableCollection<CurvePoint> Points { get; }

    private FanMode _mode;
    public FanMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsFixed));
            OnPropertyChanged(nameof(IsCurve));
            RaiseChanged();
        }
    }
    public bool IsFixed => _mode == FanMode.Fixed;
    public bool IsCurve => _mode == FanMode.Curve;

    private double _fixedPercent;
    public double FixedPercent { get => _fixedPercent; set { if (Set(ref _fixedPercent, Math.Round(Math.Clamp(value, 0, 100)))) RaiseChanged(); } }

    private string? _sourceId;
    public string? SourceId
    {
        get => _sourceId;
        set
        {
            if (!Set(ref _sourceId, value)) return;
            OnPropertyChanged(nameof(Source));
            OnPropertyChanged(nameof(SourceName));
            RaiseChanged();
        }
    }

    public SensorItem? Source => _sourceId is null ? null : _resolve(_sourceId);
    public string SourceName => Source is { } s ? $"{s.Name} · {Formatting.ShortHardwareName(s.HardwareName)}" : "Sensör seç";

    /// <summary>Kaynak sıcaklığı (eğrideki dikey çizgi için), °C.</summary>
    public double SourceTemp => Source?.Value ?? double.NaN;

    public ICommand SetModeCommand { get; }
    public ICommand PresetCommand { get; }

    /// <summary>Her ölçümde canlı değerler için çağrılır.</summary>
    public void Tick() => OnPropertyChanged(nameof(SourceTemp));

    /// <summary>Sensörler yeniden adlandırılınca başlıkları tazeler.</summary>
    public void RefreshNames()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SourceName));
    }

    public void ApplyPreset(string name)
    {
        var preset = FanPresets.ByName(name);
        _suppress = true;
        for (var i = 0; i < Points.Count && i < preset.Count; i++)
        {
            Points[i].Temp = preset[i].Temp;
            Points[i].Percent = preset[i].Percent;
        }
        _suppress = false;
        if (_mode != FanMode.Curve) Mode = FanMode.Curve;
        else RaiseChanged();
    }

    public FanCurveSettings ToSettings() => new()
    {
        Mode = _mode,
        FixedPercent = _fixedPercent,
        SourceId = _sourceId,
        Points = Points.OrderBy(p => p.Temp).Select(p => new CurvePointData(p.Temp, p.Percent)).ToList(),
    };

    private void RaiseChanged()
    {
        if (!_suppress) Changed?.Invoke(this);
    }
}
