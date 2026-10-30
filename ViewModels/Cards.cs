using Kor.Core;
using Kor.Infrastructure;

namespace Kor.ViewModels;

public enum Page { Overview, Sensors, Fans, Settings }

public enum SensorFilter { Temperatures, Fans, Disks, All }

/// <summary>Etiket + sensör çifti (kartlardaki küçük istatistikler).</summary>
public sealed record StatItem(string Label, SensorItem Sensor);

/// <summary>Bir donanım grubunun içindeki başlıklı bölüm (örn. "Fan hızları").</summary>
public sealed record SensorSection(string Title, IReadOnlyList<SensorItem> Items)
{
    public bool HasTitle => Title.Length > 0;
}

/// <summary>Sensörler sayfasındaki donanım grubu (başlık + bölümler).</summary>
public sealed record HardwareGroup(string Id, string Title, string Subtitle, HwKind Kind, IReadOnlyList<SensorSection> Sections);

/// <summary>Genel bakış sayfasındaki özet kartı.</summary>
public sealed record HardwareCard(string Title, string Subtitle, HwKind Kind, SensorItem? Primary, IReadOnlyList<StatItem> Stats, DiskHealthItem? Health = null)
{
    public bool HasPrimary => Primary is not null;
    public bool HasHealth => Health is { HasSmart: true };
}

/// <summary>Bir disk için sıcaklık, doluluk ve aktarım bilgisi.</summary>
public sealed record DiskCard(string Name, string ShortName, string HardwareId, SensorItem? Temp, SensorItem? Used, SensorItem? Activity, IReadOnlyList<StatItem> Stats, DiskHealthItem Health)
{
    public bool HasTemp => Temp is not null;
    public bool HasUsed => Used is not null;
}

/// <summary>Widget'taki büyük halkalardan biri.</summary>
public sealed class HeroGauge(string label, string caption, SensorItem temp, SensorItem? load) : ObservableObject
{
    public string Label { get; } = label;
    public string Caption { get; } = caption;
    public SensorItem Temp { get; } = temp;
    public SensorItem? Load { get; } = load;
    public bool HasLoad => Load is not null;
}

/// <summary>Genel bakıştaki grafik açıklaması (renk + ad + anlık değer).</summary>
public sealed record LegendItem(string Name, System.Windows.Media.Brush Brush, SensorItem Sensor);
