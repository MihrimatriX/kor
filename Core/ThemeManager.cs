using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using static Kor.Infrastructure.Paint;

namespace Kor.Core;

public enum AppTheme { Dark, Light, System }

/// <summary>Vurgu rengi seçeneği.</summary>
public sealed record AccentChoice(string Key, string Name, Color Color);

/// <summary>
/// Açık/koyu tema ve vurgu rengi. Renkler XAML'de DynamicResource ile bağlı olduğu için
/// Application.Resources'taki anahtarları değiştirmek tüm pencereleri anında günceller.
/// (Unity karşılığı: global bir ScriptableObject tema + UI Toolkit USS değişkenleri.)
/// </summary>
public static class ThemeManager
{
    public static readonly IReadOnlyList<AccentChoice> Accents =
    [
        new("Ember", "Kor", Color.FromRgb(0xFF, 0x7A, 0x45)),
        new("Blue", "Mavi", Color.FromRgb(0x3B, 0x82, 0xF6)),
        new("Purple", "Mor", Color.FromRgb(0x8B, 0x5C, 0xF6)),
        new("Green", "Yeşil", Color.FromRgb(0x10, 0xB9, 0x81)),
        new("Pink", "Pembe", Color.FromRgb(0xEC, 0x48, 0x99)),
        new("Amber", "Kehribar", Color.FromRgb(0xF5, 0x9E, 0x0B)),
    ];

    private static readonly Dictionary<string, string> Dark = new()
    {
        ["Window"] = "#0E1218", ["Sidebar"] = "#0A0D12", ["Card"] = "#141922", ["CardHover"] = "#19202A",
        ["Elevated"] = "#1C2330", ["Line"] = "#222A36", ["Text"] = "#EEF2F7", ["Text2"] = "#9AA4B2", ["Text3"] = "#5F6B7A",
        ["Hover"] = "#12FFFFFF", ["Pressed"] = "#1FFFFFFF", ["Track"] = "#1CFFFFFF", ["RowHover"] = "#0BFFFFFF",
        ["Subtle"] = "#0FFFFFFF", ["Subtle2"] = "#14FFFFFF", ["SubtleHover"] = "#18FFFFFF", ["Selected"] = "#17FFFFFF",
        ["MenuHover"] = "#1CFFFFFF", ["GhostHover"] = "#24FFFFFF", ["SegChecked"] = "#2BFFFFFF",
        ["ScrollThumb"] = "#2EFFFFFF", ["ScrollThumbHover"] = "#4DFFFFFF", ["ScrollThumbDrag"] = "#66FFFFFF",
        ["Control"] = "#2A3240", ["ToggleThumb"] = "#B9C2CE", ["MenuBg"] = "#171C25", ["TipBg"] = "#1C2330",
        ["FloatingBg"] = "#E6141922", ["WarnText"] = "#F3DDA4", ["WarnSoft"] = "#1AFBBF24",
        ["ChartGrid"] = "#14FFFFFF", ["ChartLabel"] = "#5F6B7A",
        ["WidgetTop"] = "#1A212C", ["WidgetBottom"] = "#0C1016",
    };

    private static readonly Dictionary<string, string> Light = new()
    {
        ["Window"] = "#F3F5F8", ["Sidebar"] = "#E9ECF1", ["Card"] = "#FFFFFF", ["CardHover"] = "#F7F8FA",
        ["Elevated"] = "#FFFFFF", ["Line"] = "#E3E7ED", ["Text"] = "#121826", ["Text2"] = "#4A5565", ["Text3"] = "#8A94A3",
        ["Hover"] = "#0D000000", ["Pressed"] = "#17000000", ["Track"] = "#16000000", ["RowHover"] = "#08000000",
        ["Subtle"] = "#0A000000", ["Subtle2"] = "#0F000000", ["SubtleHover"] = "#14000000", ["Selected"] = "#12000000",
        ["MenuHover"] = "#10000000", ["GhostHover"] = "#1A000000", ["SegChecked"] = "#FFFFFFFF",
        ["ScrollThumb"] = "#26000000", ["ScrollThumbHover"] = "#40000000", ["ScrollThumbDrag"] = "#55000000",
        ["Control"] = "#D3D9E1", ["ToggleThumb"] = "#FFFFFF", ["MenuBg"] = "#FFFFFF", ["TipBg"] = "#FFFFFF",
        ["FloatingBg"] = "#F2FFFFFF", ["WarnText"] = "#7A4E00", ["WarnSoft"] = "#24F59E0B",
        ["ChartGrid"] = "#12000000", ["ChartLabel"] = "#8A94A3",
        ["WidgetTop"] = "#FCFDFE", ["WidgetBottom"] = "#E8ECF2",
    };

    public static bool IsLight { get; private set; }

    public static event Action? Changed;

    public static void Apply(AppTheme theme, string accentKey)
    {
        var light = theme == AppTheme.Light || (theme == AppTheme.System && SystemPrefersLight());
        IsLight = light;
        var res = Application.Current.Resources;

        foreach (var (key, hex) in light ? Light : Dark)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            res["C." + key] = color;
            res["B." + key] = Frozen(color);
        }

        var accent = (Accents.FirstOrDefault(a => a.Key == accentKey) ?? Accents[0]).Color;
        res["C.Accent"] = accent;
        res["B.Accent"] = Frozen(accent);
        res["B.AccentSoft"] = Frozen(WithAlpha(accent, light ? (byte)0x22 : (byte)0x26));
        res["B.AccentHover"] = Frozen(Mix(accent, Colors.White, 0.18));
        res["B.AccentHalo"] = Frozen(WithAlpha(accent, 0x33));
        res["B.AccentBorder"] = Frozen(WithAlpha(accent, 0x66));
        res["B.AccentText"] = Frozen(light ? Mix(accent, Colors.Black, 0.25) : Mix(accent, Colors.White, 0.35));
        res["B.OnAccent"] = Frozen(Luminance(accent) > 0.55 ? Color.FromRgb(0x1C, 0x0E, 0x08) : Colors.White);

        // Cam kenarlığı ve gölge
        var edge = light ? Colors.Black : Colors.White;
        var glass = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        glass.GradientStops.Add(new GradientStop(WithAlpha(edge, light ? (byte)0x1A : (byte)0x3D), 0));
        glass.GradientStops.Add(new GradientStop(WithAlpha(edge, light ? (byte)0x10 : (byte)0x12), 0.5));
        glass.GradientStops.Add(new GradientStop(WithAlpha(edge, light ? (byte)0x0C : (byte)0x0A), 1));
        glass.Freeze();
        res["B.GlassBorder"] = glass;

        var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        sheen.GradientStops.Add(new GradientStop(WithAlpha(Colors.White, light ? (byte)0x80 : (byte)0x14), 0));
        sheen.GradientStops.Add(new GradientStop(WithAlpha(Colors.White, 0), 0.45));
        sheen.Freeze();
        res["B.Sheen"] = sheen;
        res["ShadowOpacity"] = light ? 0.18 : 0.45;

        Changed?.Invoke();
    }

    /// <summary>Windows "Uygulama modu: Açık" mı?</summary>
    public static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Grafik gibi kod içinde çizilen kontroller için güncel tema fırçası.</summary>
    public static Brush Brush(string key, Brush fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
}
