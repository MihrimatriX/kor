using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Kor.Core;

namespace Kor.Controls;

/// <summary>value == parameter ? true : false. Geri dönüşte true ise parametreyi hedef türe çevirir (RadioButton grupları için).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null) return Binding.DoNothing;
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return type.IsEnum
            ? Enum.Parse(type, parameter.ToString()!, ignoreCase: true)
            : System.Convert.ChangeType(parameter.ToString()!, type, CultureInfo.InvariantCulture);
    }
}

/// <summary>value == parameter ? Visible : Collapsed. Parametre "!" ile başlarsa tersi (value != parameter).</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var p = parameter?.ToString() ?? "";
        var negate = p.StartsWith('!');
        if (negate) p = p[1..];
        var equal = value is not null && string.Equals(value.ToString(), p, StringComparison.OrdinalIgnoreCase);
        return equal != negate ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>bool → Visibility. Parameter "invert" ise ters çevirir. null/0 da false sayılır.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value switch
        {
            bool b => b,
            int i => i > 0,
            string s => s.Length > 0,
            null => false,
            _ => true,
        };
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>HwKind → ikon geometrisi (Theme.xaml'daki "Icon.*" kaynakları).</summary>
public sealed class KindToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            HwKind.Cpu => "Icon.Cpu",
            HwKind.Gpu => "Icon.Gpu",
            HwKind.Storage => "Icon.Disk",
            HwKind.Memory => "Icon.Memory",
            HwKind.Motherboard => "Icon.Board",
            HwKind.Cooler => "Icon.Fan",
            HwKind.Psu => "Icon.Bolt",
            HwKind.Network => "Icon.Network",
            HwKind.Game => "Icon.Game",
            _ => "Icon.Chip",
        };
        return Application.Current.TryFindResource(key) as Geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>0..1 kesir → yüzde metni.</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? "%" + Math.Round(d * 100).ToString(CultureInfo.CurrentCulture) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Eşik değerini (°C) kullanıcının birimiyle gösterir. MultiBinding: [değer, UseFahrenheit].</summary>
public sealed class TemperatureTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double c) return "";
        var f = values[1] is true;
        return Formatting.Join(Formatting.Format(LibreHardwareMonitor.Hardware.SensorType.Temperature, c, f));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary>İki değer aynı nesne mi? (Seçili widget sekmesi gibi, RadioButton.IsChecked için tek yönlü.)</summary>
public sealed class SameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Length == 2 && values[0] is not null && ReferenceEquals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
