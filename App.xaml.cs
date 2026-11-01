using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Kor.Core;
using Kor.Tray;
using Kor.ViewModels;
using Kor.Views;
using Microsoft.Win32;

namespace Kor;

/// <summary>
/// Uygulama yaşam döngüsü: tek örnek → (gerekirse) yönetici olarak yeniden başlat →
/// tema + sensör servisi + FPS/oyun algılama + tepsi ikonu + widget'lar.
/// Pencere kapanınca uygulama kapanmaz (ShutdownMode=OnExplicitShutdown).
/// </summary>
public partial class App : Application
{
    private SingleInstance? _single;
    private SettingsStore? _store;
    private ISensorSource? _sensors;
    private HistoryRecorder? _recorder;
    private TrayIcon? _tray;
    private readonly Dictionary<WidgetConfig, WidgetWindow> _widgets = [];
    private DetailsWindow? _details;
    private HardwareSnapshot? _pending;
    private int _posted;
    private bool _exiting;
    private bool _demo;
    private string[] _args = [];

    private IFpsSource? _fps;
    private DispatcherTimer? _gameTimer;
    private bool _detecting;

    public static new App Current => (App)Application.Current;
    public MainViewModel ViewModel { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _args = e.Args;

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("Yakalanmamış UI hatası", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("Yakalanmamış hata", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Write("Gözlenmemiş Task hatası", ex.Exception); ex.SetObserved(); };

        // Kurulum/kaldırma sihirbazı: çalışan Kor'u düzgünce kapat (fanlar BIOS'a döner) ve çık.
        if (_args.Contains("--exit"))
        {
            SingleInstance.SignalExitAndWait(TimeSpan.FromSeconds(10));
            Shutdown();
            return;
        }

        _single = SingleInstance.TryAcquire();
        if (_single is null)
        {
            SingleInstance.SignalExisting();
            Shutdown();
            return;
        }
        _single.ExitRequested += () => Dispatcher.BeginInvoke(ExitApplication);

        _store = SettingsStore.Load();
        var settings = _store.Settings;
        var isAdmin = Elevation.IsAdministrator;
        _demo = _args.Contains("--demo");

        // Sensörlerin tamamı için yönetici gerekli. Kullanıcı UAC'yi reddederse sınırlı modda devam et.
        if (!isAdmin && !_demo && settings.AlwaysElevate && !_args.Contains("--elevated") && !_args.Contains("--no-elevate"))
        {
            _single.Dispose();
            _single = null;
            if (Elevation.TryRelaunchElevated(_args))
            {
                Shutdown();
                return;
            }
            _single = SingleInstance.TryAcquire();
            if (_single is null)
            {
                Shutdown();
                return;
            }
        }

        // Tema her pencereden önce (DynamicResource'lar ilk çizimde doğru renkle gelsin)
        ThemeManager.Apply(settings.Theme, settings.Accent);
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;

        // Geçmiş: demo verisi gerçek geçmişe karışmasın diye ayrı klasör
        var historyFolder = System.IO.Path.Combine(SettingsStore.Folder, _demo ? "history-demo" : "history");
        _recorder = new HistoryRecorder(historyFolder);

        ViewModel = _demo
            ? new MainViewModel(settings, isAdmin: true, pawnIoInstalled: true, _recorder)
            : new MainViewModel(settings, isAdmin, Elevation.IsPawnIoInstalled(), _recorder);

        _sensors = _demo ? new DemoSensorService(settings.RefreshMs) : new SensorService(settings.RefreshMs);
        _sensors.SnapshotReady += OnSnapshot;
        _sensors.SetFanPlan(ViewModel.CurrentPlan());
        ViewModel.FanPlanChanged += plan => _sensors?.SetFanPlan(plan);
        _sensors.Start();

        // Uygulama nasıl kapanırsa kapansın fanları BIOS'a geri ver, ETW oturumunu kapat.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            _sensors?.Dispose();
            _fps?.Dispose();
        };

        // Son 24 saatin kaydını arka planda oku
        _ = Task.Run(() => HistoryRecorder.Load(historyFolder, DateTime.Now.AddHours(-24)))
            .ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) Dispatcher.BeginInvoke(() => ViewModel.PrefillHistory(t.Result));
                else if (t.Exception is not null) Log.Write("Geçmiş okunamadı", t.Exception);
            });

        // Oyun algılama + FPS
        UpdateFpsSource();
        _gameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_demo ? 1 : 3) };
        _gameTimer.Tick += (_, _) => _ = DetectGameAsync();
        _gameTimer.Start();
        _ = DetectGameAsync();

        _tray = new TrayIcon(ViewModel);
        _single.ShowRequested += () => Dispatcher.BeginInvoke(() => ShowDetails());

        settings.PropertyChanged += OnSettingChanged;
        ViewModel.Widgets.CollectionChanged += (_, _) => SyncWidgetWindows();
        SyncWidgetWindows();

        // İlk açılışta (veya hiç widget görünmüyorsa) detay penceresini göster; otomatik başlatmada sessiz kal.
        if (!_args.Contains("--tray") && (_store.IsFirstRun || settings.Widgets.All(w => !w.Visible)))
            ShowDetails();

        SessionEnding += (_, _) => _store?.SaveNow();

        // Yeni sürüm denetimi: açılıştan biraz sonra, günde en fazla bir kez
        if (!_demo && settings.CheckUpdates)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                if (settings.LastUpdateCheck is { } last && DateTime.Now - last < TimeSpan.FromHours(20)) return;
                settings.LastUpdateCheck = DateTime.Now;
                await ViewModel.CheckForUpdatesAsync(notify: true);
            };
            timer.Start();
        }
    }

    /// <summary>
    /// Sensör thread'inden gelir. Sadece en son ölçümü tutup UI'a tek bir iş gönderiyoruz;
    /// UI meşgulse ölçümler birikmez (coalescing).
    /// </summary>
    private void OnSnapshot(HardwareSnapshot snapshot)
    {
        Interlocked.Exchange(ref _pending, snapshot);
        if (Interlocked.Exchange(ref _posted, 1) == 1) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _posted, 0);
            var snap = Interlocked.Exchange(ref _pending, null);
            if (snap is null || _exiting) return;

            // FPS'i "Oyun" adında sanal bir donanım olarak ekle: geçmiş, grafik, uyarı, widget hepsi bedavaya gelir.
            if (_fps is not null && ViewModel.Settings.FpsEnabled)
            {
                var reading = _fps.Read(ViewModel.CurrentGame);
                snap = snap with
                {
                    Hardware = [.. snap.Hardware, GameSensors.Sample(reading)],
                    Signature = HashCode.Combine(snap.Signature, GameSensors.HardwareId),
                };
            }
            ViewModel.Apply(snap);
        });
    }

    private async Task DetectGameAsync()
    {
        if (_detecting || _exiting) return;
        _detecting = true;
        try
        {
            var s = ViewModel.Settings;
            GameInfo? game;
            if (_fps is DemoFpsSource demo)
            {
                game = demo.DemoGame();
            }
            else
            {
                var list = s.Games.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var auto = s.AutoDetectGames;
                game = await Task.Run(() => GameDetector.Detect(list, auto));
            }
            ViewModel.SetGame(game);
            _fps?.SetGameActive(game is not null);
        }
        catch (Exception ex)
        {
            Log.Write("Oyun algılanamadı", ex);
        }
        finally
        {
            _detecting = false;
        }
    }

    private void UpdateFpsSource()
    {
        var enabled = ViewModel.Settings.FpsEnabled;
        if (!enabled)
        {
            _fps?.Dispose();
            _fps = null;
            ViewModel.SetFpsStatus("Kapalı");
            return;
        }
        if (_fps is null)
        {
            _fps = _demo ? new DemoFpsSource() : new EtwFpsMonitor(ViewModel.IsAdmin);
            _fps.SetGameActive(ViewModel.IsGameActive);
        }
        ViewModel.SetFpsStatus(_fps.Status);
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        var s = ViewModel.Settings;
        switch (e.PropertyName)
        {
            case nameof(AppSettings.RefreshMs):
                if (_sensors is not null) _sensors.IntervalMs = s.RefreshMs;
                break;
            case nameof(AppSettings.Theme):
            case nameof(AppSettings.Accent):
                ThemeManager.Apply(s.Theme, s.Accent);
                break;
            case nameof(AppSettings.FpsEnabled):
                UpdateFpsSource();
                break;
            case nameof(AppSettings.Games):
            case nameof(AppSettings.AutoDetectGames):
                _ = DetectGameAsync();
                break;
            case AppSettings.WidgetItemChanged:
                SyncWidgetWindows();
                break;
        }
    }

    private void OnSystemPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        Dispatcher.BeginInvoke(() =>
        {
            var s = ViewModel.Settings;
            if (s.Theme == AppTheme.System) ThemeManager.Apply(s.Theme, s.Accent);
        });
    }

    // ---- Widget pencereleri ----

    /// <summary>Her görünür widget ayarı için bir pencere; gizlenen/silinenlerin penceresi kapanır.</summary>
    private void SyncWidgetWindows()
    {
        if (_exiting) return;
        foreach (var (cfg, window) in _widgets.ToList())
        {
            if (!cfg.Visible || !ViewModel.Settings.Widgets.Contains(cfg))
            {
                _widgets.Remove(cfg);
                window.Close();
            }
        }
        foreach (var cfg in ViewModel.Settings.Widgets.Where(w => w.Visible))
        {
            if (_widgets.ContainsKey(cfg) || ViewModel.WidgetFor(cfg) is not { } vm) continue;
            var window = new WidgetWindow(vm);
            _widgets[cfg] = window;
            window.Closed += (_, _) =>
            {
                if (_widgets.TryGetValue(cfg, out var w) && w == window) _widgets.Remove(cfg);
                // Explorer yeniden başlarsa (sahip pencere yok olur) widget'ı geri getir.
                if (!_exiting && cfg.Visible && ViewModel.Settings.Widgets.Contains(cfg))
                    Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, SyncWidgetWindows);
            };
            window.Show();
        }
    }

    public void ShowWidget(WidgetConfig? cfg = null)
    {
        cfg ??= ViewModel.Settings.Widgets.FirstOrDefault();
        if (cfg is not null) cfg.Visible = true;
    }

    public void HideWidget(WidgetConfig cfg) => cfg.Visible = false;

    public void ShowDetails(Page? page = null)
    {
        if (page is { } p) ViewModel.CurrentPage = p;

        if (_details is null)
        {
            _details = new DetailsWindow(ViewModel);
            _details.Closed += (_, _) => _details = null;
            _details.Show();
        }
        else
        {
            if (_details.WindowState == WindowState.Minimized) _details.WindowState = WindowState.Normal;
            _details.Show();
        }
        _details.Activate();
    }

    /// <summary>Widget'ın sağ tık menüsü: önce o widget'ın ayarları, sonra genel menü.</summary>
    public System.Windows.Controls.ContextMenu? CreateContextMenu(WidgetConfig? focus) => _tray?.BuildMenu(forTray: false, focus);

    /// <summary>Menü açılmadan önce öğeleri tazeler (widget sayısı / seçimler değişmiş olabilir).</summary>
    public void RefreshMenu(System.Windows.Controls.ItemsControl menu, WidgetConfig? focus) => _tray?.Populate(menu, focus);

    // ---- Çıkış / yeniden başlatma ----

    public void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;

        _gameTimer?.Stop();
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        _store?.SaveNow();
        _details?.Close();
        foreach (var w in _widgets.Values.ToList()) w.Close();
        _widgets.Clear();
        _tray?.Dispose();
        ViewModel.ShutdownRemote();
        _sensors?.Dispose();
        _fps?.Dispose();
        _recorder?.Dispose();
        _single?.Dispose();
        _single = null;
        Shutdown();
    }

    /// <summary>UAC ile yönetici olarak yeniden başlatır.</summary>
    public void RestartElevated()
    {
        _store?.SaveNow();
        _single?.Dispose();
        _single = null;
        if (Elevation.TryRelaunchElevated(_args.Where(a => a != "--tray")))
        {
            ExitApplication();
        }
        else
        {
            _single = SingleInstance.TryAcquire();
        }
    }

    /// <summary>Aynı yetkiyle yeniden başlatır (örn. PawnIO kurulduktan sonra).</summary>
    public void Restart()
    {
        _store?.SaveNow();
        _single?.Dispose();
        _single = null;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                Arguments = Elevation.IsAdministrator ? "--elevated" : "--no-elevate",
                WorkingDirectory = AppContext.BaseDirectory,
            });
            ExitApplication();
        }
        catch (Exception ex)
        {
            Log.Write("Yeniden başlatılamadı", ex);
            _single = SingleInstance.TryAcquire();
        }
    }
}
