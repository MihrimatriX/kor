using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kor.Core;
using Kor.Infrastructure;
using LibreHardwareMonitor.Hardware;
using static Kor.Infrastructure.Paint;

namespace Kor.ViewModels;

/// <summary>Vurgu rengi seçici öğesi.</summary>
public sealed class AccentOption(string key, string name, Brush brush) : ObservableObject
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public Brush Brush { get; } = brush;

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>Disk sağlığı, fan profilleri, oyun algılama/FPS, telefondan izleme ve tema.</summary>
public sealed partial class MainViewModel
{
    // =====================================================================
    //  Disk sağlığı
    // =====================================================================

    public ObservableCollection<DiskHealthItem> DiskHealth { get; } = [];
    public bool ShowDiskHealth => _filter == SensorFilter.Disks && DiskHealth.Count > 0;

    private static DiskHealthItem DiskHealthFor(HardwareSample hw, List<SensorItem> visible, SensorItem? temp)
    {
        var item = new DiskHealthItem(hw.Id, hw.Name, temp,
            Find(visible, SensorType.Load, "Used Space"),
            Find(visible, SensorType.Level, "Remaining Life", "Life"));
        item.Update(hw.Health);
        return item;
    }

    private void SyncDiskHealth(Dictionary<string, DiskHealthItem> items)
    {
        DiskHealth.Clear();
        foreach (var item in items.Values) DiskHealth.Add(item);
        OnPropertyChanged(nameof(ShowDiskHealth));
    }

    private void UpdateDiskHealth(HardwareSnapshot snapshot)
    {
        foreach (var hw in snapshot.Hardware)
        {
            if (hw.Kind != HwKind.Storage) continue;
            foreach (var h in DiskHealth)
            {
                if (h.HardwareId == hw.Id) h.Update(hw.Health);
            }
        }
    }

    // =====================================================================
    //  Fan profilleri
    // =====================================================================

    private string _appliedProfile = "";
    private bool _overrideSuspended;

    public IReadOnlyList<string> ProfileNames => AppSettings.ProfileNames;

    /// <summary>
    /// Şu an uygulanan profil. Oyun algılanınca (ayar açıksa) "Oyun"; kullanıcı oyun sırasında elle başka profil
    /// seçerse oyun kapanana kadar onun seçimi geçerli.
    /// </summary>
    public string EffectiveProfile
    {
        get => IsGameActive && Settings.GameSwitchesFanProfile && !_overrideSuspended ? "Oyun" : Settings.ActiveFanProfile;
        set
        {
            if (string.IsNullOrEmpty(value) || !AppSettings.ProfileNames.Contains(value)) return;
            if (IsGameActive && Settings.GameSwitchesFanProfile) _overrideSuspended = value != "Oyun";
            if (value != "Oyun" || !IsGameActive) Settings.ActiveFanProfile = value;
            OnEffectiveProfileMaybeChanged();
        }
    }

    /// <summary>Fan sayfasındaki bilgi şeridi.</summary>
    public string? ProfileNote => IsGameActive && Settings.GameSwitchesFanProfile && !_overrideSuspended
        ? $"{CurrentGame!.ProcessName} çalışıyor: oyun bitene kadar Oyun profili kullanılıyor."
        : _overrideSuspended && IsGameActive
            ? $"{CurrentGame!.ProcessName} çalışıyor ama profili elle seçtin."
            : null;

    public ICommand SelectProfileCommand { get; private set; } = null!;

    public IReadOnlyList<FanControlRule> CurrentPlan() => FanCurve.PlanFrom(Settings.Profile(EffectiveProfile).Curves);

    private void OnEffectiveProfileMaybeChanged()
    {
        OnPropertyChanged(nameof(ProfileNote));
        var effective = EffectiveProfile;
        if (effective == _appliedProfile) return;
        _appliedProfile = effective;
        OnPropertyChanged(nameof(EffectiveProfile));
        RebuildFanControls();
        FanPlanChanged?.Invoke(CurrentPlan());
        ProfileChanged?.Invoke(effective);
    }

    /// <summary>Profil değişince (tepsi menüsü ve bildirim için).</summary>
    public event Action<string>? ProfileChanged;

    private static FanCurveSettings DefaultCurve(string profile) => profile switch
    {
        "Sessiz" => new FanCurveSettings { FixedPercent = 35, Points = FanPresets.Silent },
        "Oyun" => new FanCurveSettings { FixedPercent = 70, Points = FanPresets.Performance },
        _ => new FanCurveSettings(),
    };

    private void RebuildFanControls()
    {
        foreach (var fc in FanControls) fc.Changed -= OnFanChanged;
        FanControls.Clear();
        if (_snapshot is null)
        {
            OnPropertyChanged(nameof(HasFanControls));
            return;
        }

        var profileName = EffectiveProfile;
        _appliedProfile = profileName;
        var profile = Settings.Profile(profileName);
        foreach (var hw in _snapshot.Hardware)
        {
            var items = Items(hw);
            foreach (var control in items.Where(i => i.IsControllable && !i.IsHidden))
            {
                var fan = items.FirstOrDefault(i => i.Type == SensorType.Fan && i.Index == control.Index);
                var defaultSource = hw.Kind == HwKind.Gpu
                    ? PickPrimaryTemp(HwKind.Gpu, items.Where(i => i.IsTemperature).ToList())?.Id
                    : _cpuTemp?.Id;
                var settings = profile.Curves.GetValueOrDefault(control.Id) ?? DefaultCurve(profileName);
                var fc = new FanControlItem(control, fan, settings, id => _items.GetValueOrDefault(id), defaultSource);
                fc.Changed += OnFanChanged;
                FanControls.Add(fc);
            }
        }
        OnPropertyChanged(nameof(HasFanControls));
    }

    private void OnFanChanged(FanControlItem item)
    {
        Settings.SetFanCurve(EffectiveProfile, item.Control.Id, item.ToSettings());
        FanPlanChanged?.Invoke(CurrentPlan());
    }

    // =====================================================================
    //  Oyun algılama ve FPS
    // =====================================================================

    private GameInfo? _currentGame;
    public GameInfo? CurrentGame => _currentGame;
    public bool IsGameActive => _currentGame is not null;

    /// <summary>Oyun başladı/bitti (widget'ların "oyunda üstte" modu için).</summary>
    public event Action? GameChanged;

    private string _gameText = "";
    /// <summary>Genel bakıştaki oyun şeridi: "Cyberpunk2077 · 128 FPS · 1% 92".</summary>
    public string GameText { get => _gameText; private set => Set(ref _gameText, value); }

    private string _fpsStatus = "Kapalı";
    public string FpsStatus { get => _fpsStatus; private set => Set(ref _fpsStatus, value); }

    public IReadOnlyList<string> GameList => Settings.Games;

    public ICommand RemoveGameCommand { get; private set; } = null!;
    public ICommand AddGameCommand { get; private set; } = null!;

    /// <summary>App'teki algılama zamanlayıcısı çağırır (UI thread'inde).</summary>
    public void SetGame(GameInfo? game)
    {
        if (game?.ProcessId == _currentGame?.ProcessId && game?.ProcessName == _currentGame?.ProcessName) return;
        _currentGame = game;
        if (game is null) _overrideSuspended = false;

        OnPropertyChanged(nameof(CurrentGame));
        OnPropertyChanged(nameof(IsGameActive));
        UpdateGameText();
        InvalidateWidgets();
        RebuildWidgetsIfNeeded();
        OnEffectiveProfileMaybeChanged();
        GameChanged?.Invoke();
    }

    public void SetFpsStatus(string status) => FpsStatus = status;

    private void UpdateGameText()
    {
        if (_currentGame is not { } g)
        {
            GameText = "";
            return;
        }
        var parts = new List<string> { g.ProcessName };
        if (_fps?.Value is double fps) parts.Add($"{fps:0} FPS");
        if (_fpsLow?.Value is double low) parts.Add($"1% düşük {low:0}");
        GameText = string.Join(" · ", parts);
    }

    // =====================================================================
    //  Telefondan izleme
    // =====================================================================

    private RemoteServer? _remote;
    private volatile string _remoteJson = "{}";
    private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    private long _remoteRequestedAt = long.MinValue / 2;
    private long _remoteBuiltAt = long.MinValue / 2;

    /// <summary>Telefon son 5 sn içinde veri istediyse true; değilse JSON her turda boşuna kurulmaz.</summary>
    private bool RemoteWatched => _remote is not null && Environment.TickCount64 - Interlocked.Read(ref _remoteRequestedAt) < 5000;

    private string? _remoteUrl;
    public string? RemoteUrl { get => _remoteUrl; private set => Set(ref _remoteUrl, value); }

    private string _remoteStatus = "Kapalı";
    public string RemoteStatus { get => _remoteStatus; private set => Set(ref _remoteStatus, value); }

    private BitmapSource? _remoteQr;
    public BitmapSource? RemoteQr { get => _remoteQr; private set => Set(ref _remoteQr, value); }

    public ICommand NewRemoteTokenCommand { get; private set; } = null!;
    public ICommand CopyRemoteUrlCommand { get; private set; } = null!;
    public ICommand OpenRemoteUrlCommand { get; private set; } = null!;

    private void UpdateRemoteServer()
    {
        _remote?.Dispose();
        _remote = null;
        RemoteUrl = null;

        if (Settings.RemoteEnabled)
        {
            try
            {
                _remote = new RemoteServer(Settings.RemotePort, Settings.RemoteToken, RemoteJsonForRequest);
                var ip = RemoteServer.LanAddress();
                RemoteUrl = $"http://{ip ?? "localhost"}:{Settings.RemotePort}/?t={Settings.RemoteToken}";
                RemoteStatus = ip is null ? "Açık, ama yerel ağ bağlantısı bulunamadı" : "Açık";
            }
            catch (SocketException ex)
            {
                Log.Write("Uzaktan izleme sunucusu başlatılamadı", ex);
                RemoteStatus = $"Başlatılamadı: {Settings.RemotePort} portu kullanımda olabilir";
            }
        }
        else
        {
            RemoteStatus = "Kapalı";
        }
        UpdateRemoteQr();
    }

    private void UpdateRemoteQr() => RemoteQr = RemoteUrl is null ? null : RemoteServer.Qr(RemoteUrl, ThemeManager.IsLight);

    public void ShutdownRemote()
    {
        _remote?.Dispose();
        _remote = null;
    }

    private static readonly JsonSerializerOptions RemoteJsonOptions = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    /// <summary>
    /// Sunucu thread'inden çağrılır. Telefon bir süredir sormadıysa elimizdeki JSON bayattır:
    /// o zaman UI thread'inde hemen tazesini kurdururuz; sonraki isteklerde ölçüm turu zaten günceller.
    /// </summary>
    private string RemoteJsonForRequest()
    {
        var now = Environment.TickCount64;
        Interlocked.Exchange(ref _remoteRequestedAt, now);
        if (now - Interlocked.Read(ref _remoteBuiltAt) > 2500)
        {
            try { _dispatcher.Invoke(UpdateRemoteJson, System.Windows.Threading.DispatcherPriority.Normal, CancellationToken.None, TimeSpan.FromSeconds(2)); }
            catch { /* uygulama kapanıyor ya da UI meşgul: eldeki veriyle cevap ver */ }
        }
        return _remoteJson;
    }

    /// <summary>Telefon sayfasının okuduğu JSON (UI thread'inde kurulur, sunucu thread'i sadece string'i okur).</summary>
    private void UpdateRemoteJson()
    {
        if (_remote is null) return;
        Interlocked.Exchange(ref _remoteBuiltAt, Environment.TickCount64);
        try
        {
            var cfg = new WidgetConfig { ShowFps = false };
            var heroes = HeroSpecs(cfg).Select(h => new
            {
                label = h.Label,
                value = h.Temp.ValueText,
                unit = "°",
                fraction = Math.Round(h.Temp.Fraction, 3),
                color = Hex(h.Temp.Accent),
                hot = h.Temp.IsHot,
                sub = h.Load is { } l ? $"Yük {l.Display}" : h.Caption,
            });

            // Otomatik halkalarda yük zaten halkanın altında yazıyor
            var loads = Settings.PinnedSensorIds.Count == 0 ? [] : LoadSpecs(cfg);
            var rows = loads.Concat(RowSpecs(cfg)).Select(r =>
            {
                var row = new WidgetRow(r.Kind, r.Label, r.Primary, r.Secondary, r.Tertiary);
                row.Refresh();
                return new
                {
                    label = r.Label,
                    value = row.Value,
                    unit = row.Unit,
                    extra = row.Extra,
                    fraction = row.HasBar ? Math.Round(row.Fraction, 3) : 0,
                    color = Hex(row.HasBar ? row.BarBrush : row.Accent),
                    valueColor = r.Kind is WidgetRowKind.Temperature or WidgetRowKind.Disk && r.Primary.IsTemperature ? Hex(row.Accent) : null,
                };
            });

            var now = DateTime.Now;
            var series = new List<object>();
            void Line(string name, Brush brush, SensorItem? item)
            {
                if (item is null) return;
                var points = item.History.GetPoints(TimeSpan.FromMinutes(5), now);
                var step = Math.Max(1, points.Count / 100);
                var values = points.Where((_, i) => i % step == 0)
                    .Select(p => Settings.UseFahrenheit ? Math.Round(p.Value * 9 / 5 + 32, 1) : Math.Round(p.Value, 1))
                    .ToList();
                series.Add(new { name, color = Hex(brush), values });
            }
            Line("CPU", CpuLine, _cpuTemp);
            Line("GPU", GpuLine, _gpuTemp);
            Line("Disk", DiskLine, _heroDisk?.Temp);

            var data = new
            {
                machine = MachineName,
                time = now.ToString("HH:mm:ss"),
                game = _currentGame?.ProcessName,
                fps = _currentGame is null ? null : _fps?.Value,
                low1 = _currentGame is null ? null : _fpsLow?.Value,
                heroes,
                rows,
                series,
                alerts = RecentAlerts.Take(5).Select(a => new { time = a.TimeText, title = a.Title, message = a.Message }),
            };
            _remoteJson = JsonSerializer.Serialize(data, RemoteJsonOptions);
        }
        catch (Exception ex)
        {
            Log.Write("Uzaktan izleme verisi hazırlanamadı", ex);
        }
    }

    private static string Hex(Brush brush) => brush is SolidColorBrush s ? $"#{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}" : "#94A3B8";

    // =====================================================================
    //  Tema
    // =====================================================================

    public IReadOnlyList<AccentOption> AccentOptions { get; } = ThemeManager.Accents
        .Select(a => new AccentOption(a.Key, a.Name, Frozen(a.Color)))
        .ToList();

    public ICommand SelectAccentCommand { get; private set; } = null!;

    private void RefreshAccentOptions()
    {
        foreach (var o in AccentOptions) o.IsSelected = o.Key == Settings.Accent;
    }


    // =====================================================================
    //  Güncellemeler
    // =====================================================================

    private string? _updateText;
    /// <summary>Doluysa yeni sürüm var ("Yeni sürüm: v0.4.0").</summary>
    public string? UpdateText { get => _updateText; private set => Set(ref _updateText, value); }

    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }

    private string _updateUrl = UpdateChecker.ReleasesUrl;

    public ICommand CheckUpdatesCommand { get; private set; } = null!;
    public ICommand OpenUpdateCommand { get; private set; } = null!;
    public ICommand OpenRepoCommand { get; private set; } = null!;
    public ICommand OpenSiteCommand { get; private set; } = null!;

    /// <summary>Yeni sürüm bulununca (tepsi balonu için).</summary>
    public event Action<string>? UpdateAvailable;

    public async Task CheckForUpdatesAsync(bool notify)
    {
        UpdateStatus = "Denetleniyor…";
        try
        {
            var latest = await UpdateChecker.LatestAsync();
            var current = UpdateChecker.Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));
            if (latest is not null && UpdateChecker.Normalize(latest.Version) > current)
            {
                _updateUrl = latest.Url;
                UpdateText = $"Yeni sürüm: {latest.Tag}";
                UpdateStatus = "";
                if (notify) UpdateAvailable?.Invoke(latest.Tag);
            }
            else
            {
                UpdateText = null;
                UpdateStatus = "Kor güncel.";
            }
        }
        catch (Exception ex)
        {
            Log.Write("Güncelleme denetlenemedi", ex);
            UpdateStatus = "Denetlenemedi (internet bağlantısı?)";
        }
    }

    // =====================================================================

    private void InitFeatures()
    {
        CheckUpdatesCommand = new RelayCommand(() => _ = CheckForUpdatesAsync(notify: false));
        OpenUpdateCommand = new RelayCommand(() => Elevation.OpenUrl(_updateUrl));
        OpenRepoCommand = new RelayCommand(() => Elevation.OpenUrl(UpdateChecker.RepoUrl));
        OpenSiteCommand = new RelayCommand(() => Elevation.OpenUrl(UpdateChecker.SiteUrl));
        SelectProfileCommand = new RelayCommand(p => { if (p is string name) EffectiveProfile = name; });
        RemoveGameCommand = new RelayCommand(p => { if (p is string name) Settings.RemoveGame(name); });
        AddGameCommand = new RelayCommand(p => { if (p is string name) Settings.AddGame(name); });
        SelectAccentCommand = new RelayCommand(p => { if (p is string key) Settings.Accent = key; });
        NewRemoteTokenCommand = new RelayCommand(Settings.NewRemoteToken);
        CopyRemoteUrlCommand = new RelayCommand(() =>
        {
            if (RemoteUrl is null) return;
            try { Clipboard.SetText(RemoteUrl); }
            catch (Exception ex) { Log.Write("Panoya kopyalanamadı", ex); }
        });
        OpenRemoteUrlCommand = new RelayCommand(() => { if (RemoteUrl is not null) Elevation.OpenUrl(RemoteUrl); });

        RefreshAccentOptions();
        ThemeManager.Changed += UpdateRemoteQr;
        _appliedProfile = EffectiveProfile;
        UpdateRemoteServer();
    }
}
