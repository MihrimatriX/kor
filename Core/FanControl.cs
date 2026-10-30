namespace Kor.Core;

public enum FanMode
{
    /// <summary>Kontrol BIOS'ta / ekran kartı sürücüsünde (Kor dokunmaz).</summary>
    Auto,
    /// <summary>Sabit yüzde.</summary>
    Fixed,
    /// <summary>Seçilen sıcaklık sensörüne göre eğri.</summary>
    Curve,
}

/// <summary>Eğri üzerindeki bir nokta (kalıcı ayar).</summary>
public sealed record CurvePointData(double Temp, double Percent);

/// <summary>Bir fan kontrolünün kalıcı ayarı (settings.json içinde).</summary>
public sealed class FanCurveSettings
{
    public FanMode Mode { get; set; } = FanMode.Auto;
    public double FixedPercent { get; set; } = 50;
    public string? SourceId { get; set; }
    public List<CurvePointData> Points { get; set; } = FanPresets.Balanced;
}

/// <summary>Sensör thread'ine giden değişmez kural (UI ile thread paylaşımı yok).</summary>
public sealed record FanControlRule(string ControlId, FanMode Mode, float FixedPercent, string? SourceId, (float Temp, float Percent)[] Curve);

public static class FanPresets
{
    public static List<CurvePointData> Silent =>
        [new(30, 20), new(50, 25), new(65, 40), new(78, 65), new(88, 100)];

    public static List<CurvePointData> Balanced =>
        [new(30, 25), new(45, 35), new(60, 50), new(75, 75), new(85, 100)];

    public static List<CurvePointData> Performance =>
        [new(30, 35), new(45, 50), new(60, 70), new(70, 90), new(80, 100)];

    public static List<CurvePointData> ByName(string name) => name switch
    {
        "Silent" => Silent,
        "Performance" => Performance,
        _ => Balanced,
    };
}

public static class FanCurve
{
    /// <summary>Eğriyi doğrusal enterpolasyonla değerlendirir; uçların dışında düz devam eder.</summary>
    public static float Evaluate(IReadOnlyList<(float Temp, float Percent)> points, float temp)
    {
        if (points.Count == 0) return 100;
        if (temp <= points[0].Temp) return points[0].Percent;
        for (var i = 1; i < points.Count; i++)
        {
            if (temp <= points[i].Temp)
            {
                var (t0, p0) = points[i - 1];
                var (t1, p1) = points[i];
                var k = t1 - t0 <= 0 ? 1 : (temp - t0) / (t1 - t0);
                return p0 + (p1 - p0) * k;
            }
        }
        return points[^1].Percent;
    }

    public static FanControlRule ToRule(string controlId, FanCurveSettings s) => new(
        controlId,
        s.Mode,
        (float)s.FixedPercent,
        s.SourceId,
        s.Points.OrderBy(p => p.Temp).Select(p => ((float)p.Temp, (float)p.Percent)).ToArray());

    public static IReadOnlyList<FanControlRule> PlanFrom(IReadOnlyDictionary<string, FanCurveSettings> curves)
        => curves.Where(kv => kv.Value.Mode != FanMode.Auto).Select(kv => ToRule(kv.Key, kv.Value)).ToList();
}

/// <summary>
/// Sensör thread'inde çalışan fan kontrol mantığı. Güvenlik kuralları:
///  • Kaynak sensör okunamazsa fan %100'e çıkar.
///  • Değişim yumuşatılır (yukarı hızlı, aşağı yavaş) ve küçük düşüşler yok sayılır (histerezis).
///  • Kural kaldırılınca / uygulama kapanınca kontrol BIOS/sürücüye geri verilir (SetDefault).
/// </summary>
public sealed class FanController
{
    private const float MaxStepUp = 15f;
    private const float MaxStepDown = 5f;
    private const float Deadband = 2f;

    private readonly Dictionary<string, float> _applied = [];
    private readonly HashSet<string> _failed = [];

    public void Apply(IReadOnlyList<FanControlRule> plan, Func<string, float?> readValue, Func<string, IFanControl?> findControl)
    {
        var active = new HashSet<string>();

        foreach (var rule in plan)
        {
            if (rule.Mode == FanMode.Auto) continue;
            var control = findControl(rule.ControlId);
            if (control is null) continue;

            float target = rule.Mode == FanMode.Fixed
                ? rule.FixedPercent
                : readValue(rule.SourceId ?? "") is float t && float.IsFinite(t) ? FanCurve.Evaluate(rule.Curve, t) : 100f;

            target = Math.Clamp(target, Math.Max(0, control.Min), Math.Min(100, control.Max > 0 ? control.Max : 100));

            if (_applied.TryGetValue(rule.ControlId, out var last))
            {
                var delta = target - last;
                if (delta < 0 && delta > -Deadband) target = last;               // histerezis
                else if (delta > MaxStepUp) target = last + MaxStepUp;           // yumuşatma
                else if (delta < -MaxStepDown) target = last - MaxStepDown;
            }

            active.Add(rule.ControlId);
            if (_applied.TryGetValue(rule.ControlId, out var prev) && Math.Abs(prev - target) < 0.5f) continue;

            try
            {
                control.SetSoftware(target);
                _applied[rule.ControlId] = target;
            }
            catch (Exception ex)
            {
                if (_failed.Add(rule.ControlId)) Log.Write($"Fan ayarlanamadı: {rule.ControlId}", ex);
            }
        }

        // Kuralı kalkan kontrolleri varsayılana döndür.
        foreach (var id in _applied.Keys.Where(k => !active.Contains(k)).ToList())
        {
            Restore(id, findControl);
            _applied.Remove(id);
        }
    }

    public void RestoreAll(Func<string, IFanControl?> findControl)
    {
        foreach (var id in _applied.Keys.ToList()) Restore(id, findControl);
        _applied.Clear();
    }

    private static void Restore(string id, Func<string, IFanControl?> findControl)
    {
        try { findControl(id)?.SetDefault(); }
        catch (Exception ex) { Log.Write($"Fan varsayılana döndürülemedi: {id}", ex); }
    }
}

/// <summary>LibreHardwareMonitor IControl'ü (ve demo kontrolü) soyutlar.</summary>
public interface IFanControl
{
    float Min { get; }
    float Max { get; }
    void SetSoftware(float percent);
    void SetDefault();
}
