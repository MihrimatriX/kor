using System.Windows;
using System.Windows.Media;

namespace Kor.Infrastructure;

/// <summary>
/// Dondurulmuş (Freeze) fırça yardımcıları. Dondurulan fırça değişmez olur: WPF onun için değişiklik
/// takibi yapmaz ve thread'ler arasında paylaşılabilir. Unity karşılığı: paylaşılan (sharedMaterial) bir malzeme.
/// </summary>
internal static class Paint
{
    public static SolidColorBrush Frozen(byte r, byte g, byte b) => Frozen(Color.FromRgb(r, g, b));

    public static SolidColorBrush Frozen(Color color) => Frozen(new SolidColorBrush(color));

    public static T Frozen<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }
}
