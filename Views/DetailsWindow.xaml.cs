using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Kor.Core;
using Kor.Interop;
using Kor.ViewModels;

namespace Kor.Views;

/// <summary>
/// Ana pencere: genel bakış, sensörler (+ayrıntı paneli), fan kontrolü ve ayarlar.
/// Özel başlık çubuğu için WindowChrome kullanılıyor (WinUI'deki ExtendsContentIntoTitleBar'ın WPF karşılığı).
/// </summary>
public partial class DetailsWindow : Window
{
    private const double CardMinWidth = 330;
    private const double CardGap = 12;

    public static readonly DependencyProperty CardColumnsProperty = DependencyProperty.Register(
        nameof(CardColumns), typeof(int), typeof(DetailsWindow), new PropertyMetadata(2));

    /// <summary>Kart ızgarasının sütun sayısı; pencere genişliğine göre değişir.</summary>
    public int CardColumns
    {
        get => (int)GetValue(CardColumnsProperty);
        set => SetValue(CardColumnsProperty, value);
    }

    private readonly MainViewModel _vm;

    public DetailsWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        SourceInitialized += (_, _) => ApplyChrome();
        ThemeManager.Changed += ApplyChrome;
        Closed += (_, _) => ThemeManager.Changed -= ApplyChrome;
        StateChanged += (_, _) => UpdateMaximizedMargin();
        Loaded += (_, _) => vm.ChartsActive = true;
        Closed += (_, _) => vm.ChartsActive = false;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox) Close();
        };
    }

    private void ApplyChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0) Native.ApplyDarkChrome(hwnd, rounded: true, dark: !ThemeManager.IsLight);
    }

    private void UpdateMaximizedMargin()
    {
        // WindowChrome ile ekranı kaplayan pencere, kenarlık kalınlığı kadar ekran dışına taşar.
        RootBorder.Margin = WindowState == WindowState.Maximized
            ? new Thickness(
                SystemParameters.WindowResizeBorderThickness.Left + SystemParameters.WindowNonClientFrameThickness.Left - 1,
                SystemParameters.WindowResizeBorderThickness.Top + 3,
                SystemParameters.WindowResizeBorderThickness.Right + SystemParameters.WindowNonClientFrameThickness.Right - 1,
                SystemParameters.WindowResizeBorderThickness.Bottom + SystemParameters.WindowNonClientFrameThickness.Bottom - 1)
            : new Thickness(0);
    }

    private void OnPageHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var usable = e.NewSize.Width - 28 - 22 - 10; // ScrollViewer dolgusu + kaydırma çubuğu
        CardColumns = Math.Clamp((int)((usable + CardGap) / (CardMinWidth + CardGap)), 1, 4);
    }

    /// <summary>Enter: adı kaydet; Esc: vazgeç.</summary>
    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    /// <summary>Fan eğrisinin kaynak sensörünü seçmek için tema uyumlu bir menü açar.</summary>
    private void OnPickSource(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FanControlItem fan }) return;

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("Menu"),
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            FontWeight = FontWeights.Normal,
            MaxHeight = 440,
        };
        var itemStyle = (Style)FindResource("MenuItem");
        foreach (var temp in _vm.TemperatureSources)
        {
            var item = new MenuItem
            {
                Header = $"{temp.Name} · {Formatting.ShortHardwareName(temp.HardwareName)}   {temp.Display}",
                Style = itemStyle,
                IsChecked = temp.Id == fan.SourceId,
            };
            item.Click += (_, _) => fan.SourceId = temp.Id;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>Pencere başlığı olan çalışan programları listeler; seçilen oyun listesine eklenir.</summary>
    private async void OnAddGame(object sender, RoutedEventArgs e)
    {
        var apps = await Task.Run(GameDetector.RunningApps);
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("Menu"),
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            FontWeight = FontWeights.Normal,
            MaxHeight = 440,
        };
        var itemStyle = (Style)FindResource("MenuItem");
        var known = _vm.Settings.Games.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (process, title) in apps)
        {
            var shortTitle = title.Length > 48 ? title[..47] + "…" : title;
            var item = new MenuItem
            {
                Header = $"{process}   ·   {shortTitle}",
                Style = itemStyle,
                IsChecked = known.Contains(process),
            };
            item.Click += (_, _) => _vm.Settings.AddGame(process);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Pencereli program bulunamadı", Style = itemStyle, IsEnabled = false });
        menu.IsOpen = true;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
