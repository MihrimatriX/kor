using System.Globalization;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace Kor.Core;

/// <summary>Sensör değerlerini birimiyle birlikte Türkçe biçimlendirir.</summary>
public static class Formatting
{
    private static CultureInfo C => CultureInfo.CurrentCulture;

    public static (string Value, string Unit) Format(SensorType type, double v, bool fahrenheit)
    {
        switch (type)
        {
            case SensorType.Temperature:
                return fahrenheit ? ((v * 9 / 5 + 32).ToString("0", C), "°F") : (v.ToString("0", C), "°C");
            case SensorType.Fan:
                return (v.ToString("N0", C), "RPM");
            case SensorType.Control:
            case SensorType.Load:
            case SensorType.Level:
            case SensorType.Humidity:
                return ("%" + v.ToString("0", C), "");
            case SensorType.Clock:
                return v >= 1000 ? ((v / 1000).ToString("0.00", C), "GHz") : (v.ToString("0", C), "MHz");
            case SensorType.Voltage:
                return (v.ToString("0.000", C), "V");
            case SensorType.Current:
                return (v.ToString("0.00", C), "A");
            case SensorType.Power:
                return (v.ToString(v >= 100 ? "0" : "0.0", C), "W");
            case SensorType.Data: // GB
                return v >= 1024 ? ((v / 1024).ToString("0.00", C), "TB") : (v.ToString(v >= 100 ? "0" : "0.0", C), "GB");
            case SensorType.SmallData: // MB
                return v >= 1024 ? ((v / 1024).ToString("0.0", C), "GB") : (v.ToString("0", C), "MB");
            case SensorType.Throughput: // B/s
                if (v >= 1024d * 1024 * 1024) return ((v / (1024d * 1024 * 1024)).ToString("0.00", C), "GB/s");
                if (v >= 1024d * 1024) return ((v / (1024d * 1024)).ToString("0.0", C), "MB/s");
                if (v >= 1024) return ((v / 1024).ToString("0", C), "KB/s");
                return (v.ToString("0", C), "B/s");
            case SensorType.Frequency:
                return (v.ToString("0", C), "Hz");
            case SensorType.Flow:
                return (v.ToString("0", C), "L/h");
            case SensorType.Factor:
                return (v.ToString("0.00", C), "");
            case SensorType.Energy:
                return (v.ToString("0", C), "mWh");
            case SensorType.Noise:
                return (v.ToString("0", C), "dBA");
            case SensorType.TimeSpan:
                return (TimeSpan.FromSeconds(v).ToString(@"hh\:mm\:ss", C), "");
            default:
                return (v.ToString("0.##", C), "");
        }
    }

    public static string Join((string Value, string Unit) f) => f.Unit.Length == 0 ? f.Value : $"{f.Value} {f.Unit}";

    public static string TypeTitle(SensorType type) => type switch
    {
        SensorType.Temperature => "Sıcaklık",
        SensorType.Fan => "Fan hızı",
        SensorType.Control => "Fan kontrolü",
        SensorType.Load => "Yük",
        SensorType.Clock => "Saat hızı",
        SensorType.Power => "Güç",
        SensorType.Voltage => "Voltaj",
        SensorType.Current => "Akım",
        SensorType.Data => "Veri",
        SensorType.SmallData => "Bellek",
        SensorType.Throughput => "Aktarım",
        SensorType.Level => "Seviye",
        SensorType.Frequency => "Frekans",
        SensorType.Flow => "Akış",
        SensorType.Factor => "Çarpan",
        SensorType.Energy => "Enerji",
        SensorType.Noise => "Gürültü",
        SensorType.Humidity => "Nem",
        SensorType.TimeSpan => "Süre",
        _ => type.ToString(),
    };

    /// <summary>Tüm sensörler sayfasındaki bölüm sırası.</summary>
    public static int TypeOrder(SensorType type) => type switch
    {
        SensorType.Temperature => 0,
        SensorType.Fan => 1,
        SensorType.Control => 2,
        SensorType.Load => 3,
        SensorType.Clock => 4,
        SensorType.Power => 5,
        SensorType.Voltage => 6,
        SensorType.Current => 7,
        SensorType.Throughput => 8,
        SensorType.Data => 9,
        SensorType.SmallData => 10,
        SensorType.Level => 11,
        _ => 20,
    };

    public static string KindShort(HwKind kind) => kind switch
    {
        HwKind.Cpu => "CPU",
        HwKind.Gpu => "GPU",
        HwKind.Storage => "DİSK",
        HwKind.Motherboard => "ANAKART",
        HwKind.Memory => "RAM",
        HwKind.Cooler => "SOĞUTMA",
        HwKind.Psu => "PSU",
        HwKind.Network => "AĞ",
        HwKind.Game => "OYUN",
        _ => "DİĞER",
    };

    public static string KindTitle(HwKind kind) => kind switch
    {
        HwKind.Cpu => "İşlemci",
        HwKind.Gpu => "Ekran kartı",
        HwKind.Storage => "Depolama",
        HwKind.Motherboard => "Anakart",
        HwKind.Memory => "Bellek",
        HwKind.Cooler => "Soğutma",
        HwKind.Psu => "Güç kaynağı",
        HwKind.Network => "Ağ",
        HwKind.Battery => "Pil",
        HwKind.Game => "Oyun · FPS",
        _ => "Diğer",
    };

    /// <summary>"NVIDIA GeForce GTX 1080" → "GTX 1080" gibi kısaltmalar.</summary>
    public static string ShortHardwareName(string name)
    {
        string[] prefixes = ["NVIDIA GeForce ", "NVIDIA ", "AMD Radeon ", "Intel(R) ", "Intel ", "AMD "];
        foreach (var p in prefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return name[p.Length..].Replace("(TM)", "").Replace("(R)", "").Trim();
        return name.Replace("(TM)", "").Replace("(R)", "").Trim();
    }
}

/// <summary>Sıcaklık → renk. Eşikler kullanıcı ayarlarından gelir.</summary>
public static class TempScale
{
    private static readonly Color Cool = Color.FromRgb(0x38, 0xBD, 0xF8);   // gök mavisi
    private static readonly Color Good = Color.FromRgb(0x34, 0xD3, 0x99);   // nane
    private static readonly Color Warm = Color.FromRgb(0xFB, 0xBF, 0x24);   // kehribar
    private static readonly Color Hot = Color.FromRgb(0xFB, 0x7A, 0x3C);    // kor turuncusu
    private static readonly Color Critical = Color.FromRgb(0xF4, 0x3F, 0x5E); // kırmızı

    private static readonly Dictionary<uint, SolidColorBrush> Cache = [];

    public static (double Warn, double Crit) Thresholds(HwKind kind, AppSettings s) => kind switch
    {
        HwKind.Cpu => (s.CpuWarn, s.CpuWarn + 10),
        HwKind.Gpu => (s.GpuWarn, s.GpuWarn + 10),
        HwKind.Storage => (s.DiskWarn, s.DiskWarn + 10),
        HwKind.Memory => (60, 75),
        _ => (70, 85),
    };

    public static Color ColorFor(double celsius, double warn, double crit)
    {
        (double At, Color C)[] stops =
        [
            (warn * 0.50, Cool),
            (warn * 0.72, Good),
            (warn * 0.90, Warm),
            (warn, Hot),
            (crit, Critical),
        ];

        if (celsius <= stops[0].At) return stops[0].C;
        for (var i = 1; i < stops.Length; i++)
        {
            if (celsius <= stops[i].At)
            {
                var t = (celsius - stops[i - 1].At) / (stops[i].At - stops[i - 1].At);
                return Lerp(stops[i - 1].C, stops[i].C, t);
            }
        }
        return Critical;
    }

    /// <summary>Halka/çubuk doluluk oranı: 20 °C → 0, kritik+5 → 1.</summary>
    public static double Fraction(double celsius, double crit)
        => Math.Clamp((celsius - 20) / (crit + 5 - 20), 0, 1);

    public static SolidColorBrush BrushFor(Color c)
    {
        // Renkleri hafifçe nicemle ki her saniye yeni fırça oluşmasın.
        var key = (uint)((c.R & 0xFC) << 16 | (c.G & 0xFC) << 8 | (c.B & 0xFC));
        if (!Cache.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(Color.FromRgb((byte)(c.R & 0xFC), (byte)(c.G & 0xFC), (byte)(c.B & 0xFC)));
            brush.Freeze();
            Cache[key] = brush;
        }
        return brush;
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}
