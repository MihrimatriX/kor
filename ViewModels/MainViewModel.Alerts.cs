using System.Collections.ObjectModel;
using System.Media;
using System.Windows.Input;
using Kor.Core;
using Kor.Infrastructure;
using LibreHardwareMonitor.Hardware;

namespace Kor.ViewModels;

/// <summary>Uyarı geçmişindeki bir kayıt.</summary>
public sealed record AlertEntry(DateTime Time, string Title, string Message)
{
    public string TimeText => Time.ToString("HH:mm");
}

/// <summary>Ayarlar sayfasındaki "Özel eşikler" listesinin bir satırı.</summary>
public sealed record AlertRuleItem(string SensorId, string Name, string Hardware, string Condition);

/// <summary>
/// Akıllı uyarılar:
///  • Ana sıcaklıklar (CPU/GPU/disk) uyarı eşiğini 5 sn aşarsa
///  • Kullanıcının herhangi bir sensöre koyduğu özel eşik (üstüne çıkınca / altına inince)
///  • Daha önce dönen bir fan 10 sn boyunca 0 RPM okursa (ekran kartı fanları hariç: boşta durmaları normal)
///  • Disk doluluğu sınırı geçerse, SMART durumu "Dikkat"/"Kötü"ye düşerse
/// Aynı uyarı tekrar tekrar çalmasın diye her birinin bekleme süresi var.
/// </summary>
public sealed partial class MainViewModel
{
    private sealed class AlertState
    {
        public DateTime? Since;
        public bool Fired;
        public DateTime Last = DateTime.MinValue;
    }

    private readonly Dictionary<string, AlertState> _alertStates = [];
    /// <summary>Özel eşiği olan sensörler (kurallar değişince sıfırlanır).</summary>
    private HashSet<string>? _ruledIds;
    private bool _refreshingAlertEditor;
    private readonly Dictionary<string, DiskHealthStatus> _healthSeen = [];

    public ObservableCollection<AlertEntry> RecentAlerts { get; } = [];
    public bool HasRecentAlerts => RecentAlerts.Count > 0;

    public ObservableCollection<AlertRuleItem> AlertRuleItems { get; } = [];
    public bool HasAlertRules => AlertRuleItems.Count > 0;

    /// <summary>Başlık, mesaj. Tepsi ikonu balon bildirimi gösterir.</summary>
    public event Action<string, string>? Alert;

    public ICommand ClearAlertsCommand { get; private set; } = null!;
    public ICommand TestAlertCommand { get; private set; } = null!;
    public ICommand RemoveAlertRuleCommand { get; private set; } = null!;
    public ICommand SetAlertDirectionCommand { get; private set; } = null!;

    private void InitAlerts()
    {
        ClearAlertsCommand = new RelayCommand(() =>
        {
            RecentAlerts.Clear();
            OnPropertyChanged(nameof(HasRecentAlerts));
        });
        TestAlertCommand = new RelayCommand(() => Raise("Deneme uyarısı", "Uyarılar böyle görünecek."));
        RemoveAlertRuleCommand = new RelayCommand(p => { if (p is string id) Settings.SetAlertRule(id, null); });
        SetAlertDirectionCommand = new RelayCommand(p =>
        {
            if (p is AlertDirection d) SelectedAlertDirection = d;
            else if (p is string s && Enum.TryParse<AlertDirection>(s, out var parsed)) SelectedAlertDirection = parsed;
        });
        RefreshAlertEditor();
    }

    private void Raise(string title, string message)
    {
        RecentAlerts.Insert(0, new AlertEntry(DateTime.Now, title, message));
        while (RecentAlerts.Count > 30) RecentAlerts.RemoveAt(RecentAlerts.Count - 1);
        OnPropertyChanged(nameof(HasRecentAlerts));

        if (Settings.AlertSound)
        {
            try { SystemSounds.Exclamation.Play(); }
            catch { /* ses aygıtı yoksa sessiz geç */ }
        }
        Alert?.Invoke(title, message);
    }

    /// <summary>Koşul <paramref name="sustain"/> boyunca kesintisiz sürerse bir kez uyarır.</summary>
    private void Check(string key, bool condition, TimeSpan sustain, TimeSpan cooldown, Func<(string Title, string Message)> message)
    {
        if (!_alertStates.TryGetValue(key, out var st)) _alertStates[key] = st = new AlertState();
        var now = DateTime.Now;
        if (!condition)
        {
            st.Since = null;
            st.Fired = false;
            return;
        }

        st.Since ??= now;
        if (st.Fired || now - st.Since < sustain || now - st.Last < cooldown) return;
        st.Fired = true;
        st.Last = now;
        var (title, text) = message();
        Raise(title, text);
    }

    private void CheckAlerts()
    {
        if (!Settings.AlertsEnabled) return;

        var rules = Settings.AlertRules;
        var ruled = _ruledIds ??= rules.Select(r => r.SensorId).ToHashSet();

        // 1) Ana sıcaklıklar (özel eşik konduysa o geçerli)
        foreach (var (temp, hwName) in _alertTargets)
        {
            if (ruled.Contains(temp.Id)) continue;
            var (warn, _) = TempScale.Thresholds(temp.Kind, Settings);
            Check("temp:" + temp.Id, temp.Value is double v && v >= warn, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), () =>
            {
                var warnText = Formatting.Join(Formatting.Format(SensorType.Temperature, warn, Settings.UseFahrenheit));
                return ($"{Formatting.KindShort(temp.Kind)} çok ısındı", $"{hwName}: {temp.Display} (eşik {warnText})");
            });
        }

        // 2) Özel eşikler
        foreach (var rule in rules)
        {
            var item = _items.GetValueOrDefault(rule.SensorId);
            var above = rule.Direction == AlertDirection.Above;
            var hit = item?.Value is double v && (above ? v >= rule.Threshold : v <= rule.Threshold);
            Check("rule:" + rule.SensorId, hit, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), () =>
            (
                above ? $"{item!.Name} eşiği aştı" : $"{item!.Name} eşiğin altına düştü",
                $"{item.HardwareName}: {item.Display} (eşik {item.FormatValue(rule.Threshold)})"
            ));
        }

        // 3) Duran fan
        if (Settings.AlertFanStop)
        {
            foreach (var fan in _allFans)
            {
                if (fan.Kind == HwKind.Gpu || fan.IsIdle || ruled.Contains(fan.Id)) continue;
                // Kor bu fanı bilerek %0'a çektiyse uyarma
                var control = FanControls.FirstOrDefault(c => c.Fan == fan)?.Control;
                var intended = control?.Value is not double pct || pct >= 5;
                Check("fan:" + fan.Id, intended && fan.Value is double rpm && rpm < 1, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(30),
                    () => ("Fan durdu", $"{fan.Name} ({Formatting.ShortHardwareName(fan.HardwareName)}) dönmüyor: 0 RPM"));
            }
        }

        // 4) Dolmak üzere olan disk
        if (Settings.AlertDiskFull)
        {
            foreach (var disk in Disks)
            {
                Check("full:" + disk.HardwareId, disk.Used?.Value is double used && used >= Settings.DiskFullPercent, TimeSpan.Zero, TimeSpan.FromHours(12),
                    () => ("Disk dolmak üzere", $"{disk.Name}: {disk.Used!.Display} dolu"));
            }
        }

        // 5) SMART durumu kötüleşti
        foreach (var h in DiskHealth)
        {
            if (h.Status is not (DiskHealthStatus.Caution or DiskHealthStatus.Bad)) continue;
            if (_healthSeen.TryGetValue(h.HardwareId, out var seen) && seen >= h.Status) continue;
            _healthSeen[h.HardwareId] = h.Status;
            Raise($"Disk sağlığı: {h.StatusText}", $"{h.Name}: SMART durumu \"{h.StatusText}\". Önemli dosyalarını yedekle.");
        }
    }

    // =====================================================================
    //  Sensör detay panelindeki uyarı düzenleyici
    // =====================================================================

    private AlertRule? RuleFor(string id) => Settings.AlertRules.FirstOrDefault(r => r.SensorId == id);
    private AlertRule? SelectedRule => _selectedSensor is { } s ? RuleFor(s.Id) : null;

    public bool SelectedAlertEnabled
    {
        get => SelectedRule is not null;
        set
        {
            if (_selectedSensor is not { } s || value == SelectedAlertEnabled) return;
            if (!value)
            {
                Settings.SetAlertRule(s.Id, null);
                return;
            }
            var (min, max, step) = AlertRange(s);
            var (dir, threshold) = DefaultRule(s);
            Settings.SetAlertRule(s.Id, new AlertRule { SensorId = s.Id, Direction = dir, Threshold = Snap(threshold, min, max, step) });
        }
    }

    public AlertDirection SelectedAlertDirection
    {
        get => SelectedRule?.Direction ?? AlertDirection.Above;
        set
        {
            if (SelectedRule is not { } r || r.Direction == value) return;
            Settings.SetAlertRule(r.SensorId, new AlertRule { SensorId = r.SensorId, Direction = value, Threshold = r.Threshold });
        }
    }

    public double SelectedAlertThreshold
    {
        get => SelectedRule?.Threshold ?? 0;
        set
        {
            // Sensör değişirken kaydırıcının Min/Max'ı değişip değeri "kırpabilir"; o geri yazımı yok say.
            if (_refreshingAlertEditor || SelectedRule is not { } r || _selectedSensor is not { } s) return;
            var (min, max, step) = AlertRange(s);
            var snapped = Snap(value, min, max, step);
            if (Math.Abs(snapped - r.Threshold) < step / 2) return;
            Settings.SetAlertRule(r.SensorId, new AlertRule { SensorId = r.SensorId, Direction = r.Direction, Threshold = snapped });
        }
    }

    public string SelectedAlertText => SelectedRule is { } r && _selectedSensor is { } s ? s.FormatValue(r.Threshold) : "";
    public double AlertMin => _selectedSensor is { } s ? AlertRange(s).Min : 0;
    public double AlertMax => _selectedSensor is { } s ? AlertRange(s).Max : 100;
    public double AlertStep => _selectedSensor is { } s ? AlertRange(s).Step : 1;

    private void RefreshAlertEditor()
    {
        _refreshingAlertEditor = true;
        try
        {
            OnPropertyChanged(nameof(AlertMin));
            OnPropertyChanged(nameof(AlertMax));
            OnPropertyChanged(nameof(AlertStep));
            OnPropertyChanged(nameof(SelectedAlertThreshold));
        }
        finally
        {
            _refreshingAlertEditor = false;
        }
        OnPropertyChanged(nameof(SelectedAlertEnabled));
        OnPropertyChanged(nameof(SelectedAlertDirection));
        OnPropertyChanged(nameof(SelectedAlertText));

        AlertRuleItems.Clear();
        foreach (var r in Settings.AlertRules)
        {
            var item = _items.GetValueOrDefault(r.SensorId);
            var name = item?.Name ?? "Bulunamayan sensör";
            var value = item?.FormatValue(r.Threshold) ?? r.Threshold.ToString("0.##");
            AlertRuleItems.Add(new AlertRuleItem(r.SensorId, name, item?.HardwareName ?? "",
                r.Direction == AlertDirection.Above ? $"{value} üstünde" : $"{value} altında"));
        }
        OnPropertyChanged(nameof(HasAlertRules));
    }

    /// <summary>Sensör türüne göre kaydırıcı aralığı (ham birimde).</summary>
    private static (double Min, double Max, double Step) AlertRange(SensorItem s)
    {
        var seen = Math.Max(Math.Abs(s.SessionMax ?? 0), Math.Abs(s.Value ?? 0));
        if (s.Unit == "FPS") return (0, 360, 5);
        if (s.Unit == "ms") return (0, 100, 1);
        return s.Type switch
        {
            SensorType.Temperature => (20, 110, 1),
            SensorType.Fan => (0, RoundUp(Math.Max(3000, seen * 1.5), 100), 50),
            SensorType.Load or SensorType.Level or SensorType.Control or SensorType.Humidity => (0, 100, 1),
            SensorType.Power => (0, RoundUp(Math.Max(300, seen * 2), 10), 5),
            SensorType.Clock => (0, RoundUp(Math.Max(6000, seen * 1.5), 100), 50),
            SensorType.Voltage => (0, Math.Max(2, Math.Ceiling(seen * 1.5)), 0.01),
            SensorType.Throughput => (0, RoundUp(Math.Max(100e6, seen * 2), 1e6), 1e6),
            SensorType.Data or SensorType.SmallData => (0, RoundUp(Math.Max(64, seen * 2), 1), 0.5),
            _ => (0, RoundUp(Math.Max(100, seen * 2), 10), Math.Max(0.01, RoundUp(Math.Max(100, seen * 2), 10) / 200)),
        };
    }

    private (AlertDirection Direction, double Threshold) DefaultRule(SensorItem s)
    {
        if (s.Unit == "FPS") return (AlertDirection.Below, 30);
        var v = s.Value ?? 0;
        return s.Type switch
        {
            SensorType.Temperature when s.Kind is HwKind.Cpu or HwKind.Gpu or HwKind.Storage => (AlertDirection.Above, TempScale.Thresholds(s.Kind, Settings).Warn),
            SensorType.Temperature => (AlertDirection.Above, Math.Round(v + 15)),
            SensorType.Fan => (AlertDirection.Below, 300),
            SensorType.Load or SensorType.Level or SensorType.Control => (AlertDirection.Above, 90),
            _ => (AlertDirection.Above, v * 1.25),
        };
    }

    private static double Snap(double value, double min, double max, double step)
        => Math.Clamp(Math.Round(value / step) * step, min, max);

    private static double RoundUp(double value, double unit) => Math.Ceiling(value / unit) * unit;
}
