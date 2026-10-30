using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kor.Core;
using Kor.Interop;
using Kor.ViewModels;

namespace Kor.Views;

/// <summary>
/// Masaüstü widget'ı. Unity'deki "her şeyin üstünde duran bir UI Canvas" yerine burada
/// Win32 pencere stilleriyle oynuyoruz:
///  • WS_EX_TOOLWINDOW → Alt+Tab'da görünmez
///  • WS_EX_NOACTIVATE → tıklayınca odak çalmaz / öne gelmez
///  • Masaüstü modu: sahibi Progman (masaüstü) + z-sırası hep en altta → Win+D'de de görünür kalır
///  • Tıklama geçirgen: WS_EX_TRANSPARENT
/// </summary>
public partial class WidgetWindow : Window
{
    private readonly WidgetViewModel _vm;
    private readonly WidgetConfig _cfg;
    private readonly DispatcherTimer _saveTimer;
    private nint _hwnd;
    private bool _positioned;
    private bool _onTop;
    private long _lastDownTicks;
    private Point _lastDownPos;

    public WidgetWindow(WidgetViewModel vm)
    {
        _vm = vm;
        _cfg = vm.Config;
        DataContext = vm;
        InitializeComponent();

        // Kayıtlı konum geçerliyse göstermeden önce uygula (ilk karede zıplama olmasın).
        if (_cfg.Left is double l && _cfg.Top is double t && IsOnScreen(l, t))
        {
            Left = l;
            Top = t;
        }
        else
        {
            Left = -10_000;
            Top = -10_000;
        }

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _cfg.Left = Math.Round(Left);
            _cfg.Top = Math.Round(Top);
        };

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        LocationChanged += (_, _) =>
        {
            if (!_positioned) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        MouseLeftButtonDown += OnMouseDown;
        SizeChanged += (_, e) =>
        {
            if (_positioned && e.PreviousSize.Width > 0) KeepOnScreen(e.PreviousSize, e.NewSize);
        };
        ContextMenu = App.Current.CreateContextMenu(_cfg);
        ContextMenuOpening += (_, _) => { if (ContextMenu is not null) App.Current.RefreshMenu(ContextMenu, _cfg); };

        _cfg.PropertyChanged += OnConfigChanged;
        vm.Main.GameChanged += ApplyPlacement;
        Closed += (_, _) =>
        {
            _cfg.PropertyChanged -= OnConfigChanged;
            vm.Main.GameChanged -= ApplyPlacement;
            _saveTimer.Stop();
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        ApplyPlacement();
        ApplyClickThrough();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Left < -9_000)
        {
            // İlk açılış: ilk widget sağ üstte; sonradan eklenen şerit sol üstte,
            // diğerleri ilk widget'ın soluna doğru sıralanır (üst üste binmesinler).
            var wa = SystemParameters.WorkArea;
            var index = Math.Max(0, App.Current.ViewModel.Settings.Widgets.IndexOf(_cfg));
            if (index == 0)
            {
                Left = wa.Right - ActualWidth - 8;
                Top = wa.Top + 8;
            }
            else if (_cfg.Mode == WidgetMode.Strip)
            {
                Left = wa.Left + 8;
                Top = wa.Top + 8;
            }
            else
            {
                Left = Math.Max(wa.Left, wa.Right - (ActualWidth + 8) * (index + 1));
                Top = wa.Top + 8;
            }
        }
        else
        {
            var size = new Size(ActualWidth, ActualHeight);
            KeepOnScreen(size, size);
        }
        _positioned = true;

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WidgetConfig.Placement):
                ApplyPlacement();
                break;
            case nameof(WidgetConfig.ClickThrough):
                ApplyClickThrough();
                break;
        }
    }

    /// <summary>"Oyunda üstte": oyun algılanınca her şeyin üstüne çıkar (FPS overlay), oyun bitince masaüstüne döner.</summary>
    private bool WantsTop => _cfg.Placement == WidgetPlacement.Topmost
                             || (_cfg.Placement == WidgetPlacement.GameTopmost && _vm.Main.IsGameActive);

    private void ApplyPlacement()
    {
        if (_hwnd == 0) return;
        _onTop = WantsTop;

        if (_onTop)
        {
            Native.SetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT, 0);
            Topmost = false; // WPF bayrağı değişmezse SetWindowPos atlanır; zorla yenile
            Topmost = true;
        }
        else
        {
            Topmost = false;
            // Sahibi masaüstü (Progman) olan pencereler "Masaüstünü göster"de gizlenmez.
            var progman = Native.FindWindow("Progman", null);
            if (progman != 0) Native.SetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT, progman);
            Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == 0) return;
        if (_cfg.ClickThrough) Native.AddExStyle(_hwnd, Native.WS_EX_TRANSPARENT);
        else Native.RemoveExStyle(_hwnd, Native.WS_EX_TRANSPARENT);
    }

    /// <summary>Masaüstü modunda z-sırası değişmeye çalışırsa pencereyi en alta zorla.</summary>
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Native.WM_WINDOWPOSCHANGING && !_onTop)
        {
            var pos = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
            if ((pos.flags & Native.SWP_NOZORDER) == 0)
            {
                pos.hwndInsertAfter = Native.HWND_BOTTOM;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return 0;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // DragMove modal bir döngü başlattığı için WPF'in ClickCount'u güvenilmez;
        // çift tıklamayı zamana ve konuma bakarak kendimiz algılıyoruz.
        // Not: DragMove sonrası e.GetPosition bayat kalabiliyor; imleci doğrudan sistemden oku.
        var now = Environment.TickCount64;
        var cursor = System.Windows.Forms.Cursor.Position;
        var pos = new Point(cursor.X, cursor.Y);
        if (now - _lastDownTicks <= System.Windows.Forms.SystemInformation.DoubleClickTime
            && Math.Abs(pos.X - _lastDownPos.X) < 6 && Math.Abs(pos.Y - _lastDownPos.Y) < 6)
        {
            _lastDownTicks = 0;
            App.Current.ShowDetails();
            return;
        }
        _lastDownTicks = now;
        _lastDownPos = pos;

        if (_cfg.Locked) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Fare bırakılmışsa DragMove fırlatır; yoksay.
        }
    }

    /// <summary>
    /// Görünüm değişince (ör. şerit moduna geçince) boyut değişir. Widget sağ kenara yakınsa sağdan
    /// hizalı kalsın, her durumda bulunduğu ekranın çalışma alanında kalsın.
    /// </summary>
    private void KeepOnScreen(Size oldSize, Size newSize)
    {
        var wa = WorkArea();
        var oldRight = Left + oldSize.Width;
        var rightGap = wa.Right - oldRight;
        if (rightGap is >= -40 and < 80) Left = wa.Right - newSize.Width - Math.Max(0, rightGap);

        if (Left + newSize.Width > wa.Right) Left = wa.Right - newSize.Width;
        if (Left < wa.Left) Left = wa.Left;
        if (Top + newSize.Height > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - newSize.Height);
    }

    /// <summary>Pencerenin bulunduğu monitörün çalışma alanı (DIP cinsinden).</summary>
    private Rect WorkArea()
    {
        try
        {
            var px = System.Windows.Forms.Screen.FromHandle(_hwnd).WorkingArea;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is { } target)
            {
                var m = target.TransformFromDevice;
                var tl = m.Transform(new Point(px.Left, px.Top));
                var br = m.Transform(new Point(px.Right, px.Bottom));
                return new Rect(tl, br);
            }
        }
        catch
        {
            // Yedek: birincil ekran
        }
        return SystemParameters.WorkArea;
    }

    private static bool IsOnScreen(double left, double top)
    {
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        return left > vl - 200 && left < vl + vw - 60 && top > vt - 20 && top < vt + vh - 60;
    }

    private void OnDetails(object sender, RoutedEventArgs e) => App.Current.ShowDetails();
    private void OnSettings(object sender, RoutedEventArgs e) => App.Current.ShowDetails(Page.Settings);
    private void OnHide(object sender, RoutedEventArgs e) => App.Current.HideWidget(_cfg);

    private void OnHintClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        App.Current.ShowDetails();
    }
}
