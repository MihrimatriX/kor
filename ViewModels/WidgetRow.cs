using System.Windows.Media;
using Kor.Core;
using Kor.Infrastructure;

namespace Kor.ViewModels;

public enum WidgetRowKind { Temperature, Load, Memory, Network, Fan, Disk, Fps, Other }

/// <summary>
/// Widget'taki tek satır (liste/şerit modunda her şey, halka modunda alt özet).
/// Bir ya da iki sensörden beslenir; her ölçümde <see cref="Refresh"/> ile güncellenir.
/// </summary>
public sealed class WidgetRow(WidgetRowKind kind, string label, SensorItem primary, SensorItem? secondary = null, SensorItem? tertiary = null) : ObservableObject
{
    private static readonly Brush NeutralBar = Frozen(0x8E, 0xA2, 0xBD);

    public WidgetRowKind Kind { get; } = kind;
    public string Label { get; } = label;
    public SensorItem Primary { get; } = primary;
    public SensorItem? Secondary { get; } = secondary;
    public SensorItem? Tertiary { get; } = tertiary;
    public bool HasBar => Kind is not WidgetRowKind.Network and not WidgetRowKind.Other and not WidgetRowKind.Fps;

    private string _value = "—";
    public string Value { get => _value; private set => Set(ref _value, value); }

    private string _unit = "";
    public string Unit { get => _unit; private set => Set(ref _unit, value); }

    private string _extra = "";
    /// <summary>İkincil bilgi (RAM için "9,4 / 16 GB", ağ için yükleme hızı, disk için doluluk).</summary>
    public string Extra { get => _extra; private set => Set(ref _extra, value); }

    private double _fraction;
    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }

    private Brush _accent = Brushes.White;
    public Brush Accent { get => _accent; private set => Set(ref _accent, value); }

    private Brush _barBrush = NeutralBar;
    public Brush BarBrush { get => _barBrush; private set => Set(ref _barBrush, value); }

    public void Refresh()
    {
        var p = Primary;
        switch (Kind)
        {
            case WidgetRowKind.Temperature:
                Value = p.Value is null ? "—" : p.ValueText;
                Unit = "°";
                Extra = "";
                Fraction = p.Fraction;
                Accent = p.Accent;
                BarBrush = p.Accent;
                break;

            case WidgetRowKind.Memory:
                // Primary = yük (%), Secondary = kullanılan (GB), Tertiary = boş (GB)
                Value = p.Display;
                Unit = "";
                Extra = Secondary?.Value is double used && Tertiary?.Value is double free
                    ? $"{used.ToString(used >= 100 ? "0" : "0.0")} / {(used + free).ToString(used + free >= 100 ? "0" : "0.0")} GB"
                    : "";
                Fraction = p.Fraction;
                Accent = p.Accent;
                BarBrush = p.Accent;
                break;

            case WidgetRowKind.Network:
                // Primary = indirme, Secondary = yükleme
                Value = "↓ " + p.Display;
                Unit = "";
                Extra = Secondary is null ? "" : "↑ " + Secondary.Display;
                Fraction = 0;
                Accent = p.Accent;
                break;

            case WidgetRowKind.Fan:
                Value = p.ValueText;
                Unit = p.UnitText;
                Extra = "";
                Fraction = p.Fraction;
                Accent = p.Accent;
                BarBrush = p.Accent;
                break;

            case WidgetRowKind.Disk:
                // Primary = sıcaklık (varsa), Secondary = doluluk
                Value = p.IsTemperature ? (p.Value is null ? "—" : p.ValueText) : p.Display;
                Unit = p.IsTemperature ? "°" : "";
                Extra = Secondary?.Display ?? "";
                Fraction = Secondary?.Fraction ?? 0;
                Accent = p.Accent;
                BarBrush = NeutralBar;
                break;

            case WidgetRowKind.Fps:
                // Primary = FPS, Secondary = 1% düşük
                Value = p.Value is null ? "—" : p.ValueText;
                Unit = "FPS";
                Extra = Secondary?.Value is null ? "" : "1% " + Secondary.ValueText;
                Fraction = 0;
                Accent = p.Accent;
                break;

            default: // Load, Other
                Value = p.ValueText;
                Unit = p.UnitText;
                Extra = "";
                Fraction = p.Fraction;
                Accent = p.Accent;
                BarBrush = p.Accent;
                break;
        }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
