using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using Kor.Core;
using Kor.ViewModels;
using Page = Kor.ViewModels.Page;
using WinForms = System.Windows.Forms;
using Wpf = System.Windows.Controls;

namespace Kor.Tray;

/// <summary>
/// Bildirim alanı ikonu. İkon WinForms NotifyIcon, menü ise temaya uygun WPF ContextMenu.
/// İsteğe bağlı olarak ikonda CPU sıcaklığı canlı yazılır.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly WinForms.NotifyIcon _icon;
    private readonly Icon _appIcon;
    private readonly Wpf.ContextMenu _menu;
    private readonly string? _fontName;

    private nint _dynamicHandle;
    private Icon? _dynamicIcon;
    private string? _lastIconKey;
    private string? _lastTooltip;

    public TrayIcon(MainViewModel vm)
    {
        _vm = vm;
        _appIcon = LoadAppIcon();
        _fontName = PickFont();

        _icon = new WinForms.NotifyIcon
        {
            Icon = _appIcon,
            Text = "Kor — sensörler okunuyor…",
            Visible = true,
        };
        _icon.MouseUp += OnMouseUp;
        _icon.BalloonTipClicked += (_, _) => App.Current.ShowDetails(Page.Overview);

        _menu = BuildMenu(forTray: true);

        _vm.Updated += OnUpdated;
        _vm.Alert += (title, message) => _icon.ShowBalloonTip(6000, title, message, WinForms.ToolTipIcon.Warning);
        _vm.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.TrayShowsTemp)) OnUpdated();
        };
        _vm.UpdateAvailable += tag =>
            _icon.ShowBalloonTip(6000, "Kor güncellemesi", $"{tag} yayımlandı. Ayarlar › Hakkında'dan indirebilirsin.", WinForms.ToolTipIcon.Info);
        _vm.ProfileChanged += profile =>
        {
            if (_vm.IsGameActive && profile == "Oyun" && _vm.HasFanControls)
                _icon.ShowBalloonTip(3000, "Oyun profili", $"{_vm.CurrentGame?.ProcessName} algılandı, fanlar Oyun profiline geçti.", WinForms.ToolTipIcon.Info);
        };
    }

    private void OnMouseUp(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Left) App.Current.ShowDetails();
        else if (e.Button == WinForms.MouseButtons.Right) ShowMenu();
    }

    // ---- Menü ----

    /// <summary>
    /// Tepsi ve widget'lar aynı menü kurucusunu kullanır; her biri kendi örneğini alır.
    /// Widget sayısı değişebildiği için öğeler her açılışta yeniden kurulur.
    /// </summary>
    /// <param name="focus">Widget'ın kendi menüsüyse o widget (ayarları en üstte gösterilir).</param>
    public Wpf.ContextMenu BuildMenu(bool forTray, WidgetConfig? focus = null)
    {
        var menu = new Wpf.ContextMenu
        {
            Style = (Style)Application.Current.Resources["Menu"],
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };
        Populate(menu, focus);
        menu.Opened += (_, _) =>
        {
            // Tepsiden açılan menü, dışına tıklanınca kapanabilsin diye ön plana alınmalı.
            if (forTray && PresentationSource.FromVisual(menu) is HwndSource src) SetForegroundWindow(src.Handle);
        };
        return menu;
    }

    public void Populate(ItemsControl menu, WidgetConfig? focus)
    {
        var res = Application.Current.Resources;
        var itemStyle = (Style)res["MenuItem"];
        var sepStyle = (Style)res["MenuSeparator"];
        var s = _vm.Settings;
        menu.Items.Clear();

        MenuItem Item(string header, Action onClick, bool isChecked = false)
        {
            var mi = new MenuItem { Header = header, Style = itemStyle, IsChecked = isChecked };
            mi.Click += (_, _) => onClick();
            return mi;
        }

        MenuItem Check(string header, object source, string path)
        {
            var mi = new MenuItem { Header = header, Style = itemStyle, IsCheckable = true, StaysOpenOnClick = false };
            mi.SetBinding(MenuItem.IsCheckedProperty, new Binding(path) { Source = source, Mode = BindingMode.TwoWay });
            return mi;
        }

        Separator Sep() => new() { Style = sepStyle };

        void AddWidgetItems(ItemCollection items, WidgetConfig w, bool withVisibility)
        {
            if (withVisibility) items.Add(Check("Göster", w, nameof(WidgetConfig.Visible)));
            items.Add(Item("Halkalar", () => w.Mode = WidgetMode.Rings, w.Mode == WidgetMode.Rings));
            items.Add(Item("Liste", () => w.Mode = WidgetMode.List, w.Mode == WidgetMode.List));
            items.Add(Item("Şerit", () => w.Mode = WidgetMode.Strip, w.Mode == WidgetMode.Strip));
            items.Add(Sep());
            items.Add(Item("Masaüstünde dur", () => w.Placement = WidgetPlacement.Desktop, w.Placement == WidgetPlacement.Desktop));
            items.Add(Item("Her zaman üstte", () => w.Placement = WidgetPlacement.Topmost, w.Placement == WidgetPlacement.Topmost));
            items.Add(Item("Oyunda üstte", () => w.Placement = WidgetPlacement.GameTopmost, w.Placement == WidgetPlacement.GameTopmost));
            items.Add(Sep());
            items.Add(Check("Konumu kilitle", w, nameof(WidgetConfig.Locked)));
            items.Add(Check("Tıklamaları geçir", w, nameof(WidgetConfig.ClickThrough)));
        }

        menu.Items.Add(Item("Kor'u aç", () => App.Current.ShowDetails()));
        menu.Items.Add(Sep());

        if (focus is not null)
        {
            AddWidgetItems(menu.Items, focus, withVisibility: false);
            menu.Items.Add(Item("Bu widget'ı gizle", () => App.Current.HideWidget(focus)));
        }
        else if (s.Widgets.Count == 1)
        {
            var only = s.Widgets[0];
            menu.Items.Add(Check("Widget'ı göster", only, nameof(WidgetConfig.Visible)));
            AddWidgetItems(menu.Items, only, withVisibility: false);
        }
        else
        {
            foreach (var w in s.Widgets)
            {
                var sub = new MenuItem { Header = w.Visible ? w.Name : $"{w.Name} (gizli)", Style = itemStyle };
                AddWidgetItems(sub.Items, w, withVisibility: true);
                menu.Items.Add(sub);
            }
        }
        if (s.Widgets.Count < AppSettings.MaxWidgets)
        {
            menu.Items.Add(Item("Yeni widget ekle", () =>
            {
                _vm.AddWidgetCommand.Execute(null);
                App.Current.ShowDetails(Page.Settings);
            }));
        }

        // Fan profilleri (sadece kontrol edilebilir fan varsa anlamlı)
        if (_vm.HasFanControls)
        {
            menu.Items.Add(Sep());
            var profiles = new MenuItem { Header = $"Fan profili: {_vm.EffectiveProfile}", Style = itemStyle };
            foreach (var name in AppSettings.ProfileNames)
                profiles.Items.Add(Item(name, () => _vm.EffectiveProfile = name, _vm.EffectiveProfile == name));
            profiles.Items.Add(Sep());
            profiles.Items.Add(Check("Oyunda Oyun profiline geç", s, nameof(AppSettings.GameSwitchesFanProfile)));
            menu.Items.Add(profiles);
        }

        menu.Items.Add(Sep());
        menu.Items.Add(Item("Ayarlar", () => App.Current.ShowDetails(Page.Settings)));
        menu.Items.Add(Item("Çıkış", () => App.Current.ExitApplication()));
    }

    private void ShowMenu()
    {
        _menu.IsOpen = false;
        Populate(_menu, null);
        _menu.IsOpen = true;
    }

    // ---- İkon ve araç ipucu ----

    private void OnUpdated()
    {
        UpdateTooltip();

        var cpu = _vm.CpuTemp ?? _vm.GpuTemp;
        if (!_vm.Settings.TrayShowsTemp || cpu?.Value is not double celsius)
        {
            if (_lastIconKey is not null)
            {
                _icon.Icon = _appIcon;
                _lastIconKey = null;
                DisposeDynamic();
            }
            return;
        }

        var text = cpu.ValueText;
        var brush = cpu.Accent as System.Windows.Media.SolidColorBrush;
        var color = brush?.Color ?? System.Windows.Media.Colors.White;
        var key = $"{text}|{color}";
        if (key == _lastIconKey) return;
        _lastIconKey = key;

        var size = WinForms.SystemInformation.SmallIconSize.Width;
        var newHandle = RenderTempIcon(text, Color.FromArgb(color.R, color.G, color.B), size);
        var newIcon = Icon.FromHandle(newHandle);
        _icon.Icon = newIcon;

        DisposeDynamic();
        _dynamicIcon = newIcon;
        _dynamicHandle = newHandle;
    }

    private void UpdateTooltip()
    {
        var parts = new List<string> { "Kor" };
        if (_vm.CpuTemp is { } c) parts.Add($"CPU {c.Display}");
        if (_vm.GpuTemp is { } g) parts.Add($"GPU {g.Display}");
        if (_vm.HeroDiskTemp is { } d) parts.Add($"Disk {d.Display}");
        var text = string.Join("\n", parts);
        if (text.Length > 127) text = text[..127];
        if (text == _lastTooltip) return;
        _lastTooltip = text;
        _icon.Text = text;
    }

    private nint RenderTempIcon(string text, Color color, int size)
    {
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // Açık görev çubuğunda da okunsun diye koyu, yuvarlak bir zemin
            using (var bg = new SolidBrush(Color.FromArgb(235, 14, 18, 24)))
            using (var path = RoundedRect(new RectangleF(0, 0, size, size), size * 0.28f))
                g.FillPath(bg, path);

            // Alt kenarda sıcaklık renginde ince çizgi
            using (var bar = new SolidBrush(color))
                g.FillRectangle(bar, size * 0.2f, size - Math.Max(2, size / 9f) - 1, size * 0.6f, Math.Max(2, size / 9f));

            var fontSize = size * (text.Length >= 3 ? 0.52f : 0.66f);
            using var font = _fontName is null
                ? new Font(System.Drawing.FontFamily.GenericSansSerif, fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel)
                : new Font(_fontName, fontSize, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.FromArgb(245, 245, 248));
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(text, font, brush, new RectangleF(-1, -size * 0.06f, size + 2, size), format);
        }
        return bmp.GetHicon();
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>GDI+ bilinmeyen yazı tipinde sessizce başka fonta düşer; o yüzden ismi kontrol ediyoruz.</summary>
    private static string? PickFont()
    {
        string[] candidates = ["Bahnschrift SemiBold SemiConden", "Bahnschrift SemiBold", "Bahnschrift", "Segoe UI Semibold"];
        using var installed = new InstalledFontCollection();
        var names = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.FirstOrDefault(names.Contains);
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/kor.ico"));
            if (info is not null)
            {
                using var stream = info.Stream;
                return new Icon(stream, WinForms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Uygulama ikonu yüklenemedi", ex);
        }
        return SystemIcons.Application;
    }

    private void DisposeDynamic()
    {
        _dynamicIcon?.Dispose();
        _dynamicIcon = null;
        if (_dynamicHandle != 0)
        {
            DestroyIcon(_dynamicHandle);
            _dynamicHandle = 0;
        }
    }

    public void Dispose()
    {
        _vm.Updated -= OnUpdated;
        _icon.Visible = false;
        _icon.Dispose();
        DisposeDynamic();
        _appIcon.Dispose();
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint handle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
}
