using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Kor.Infrastructure;

namespace Kor.Core;

public enum WidgetPlacement
{
    /// <summary>Pencerelerin arkasında, masaüstü üzerinde durur.</summary>
    Desktop,
    /// <summary>Her zaman diğer pencerelerin üstünde.</summary>
    Topmost,
    /// <summary>Normalde masaüstünde; oyun algılanınca üste çıkar (FPS overlay).</summary>
    GameTopmost,
}

public enum WidgetMode
{
    /// <summary>Büyük sıcaklık halkaları + kısa özet satırları.</summary>
    Rings,
    /// <summary>Halkasız, tek sütun liste.</summary>
    List,
    /// <summary>Tek satırlık yatay şerit.</summary>
    Strip,
}

public enum AlertDirection { Above, Below }

/// <summary>Bir widget'ın ayarları. Birden fazla widget olabilir; her birinin görünümü ve içeriği ayrı.</summary>
public sealed class WidgetConfig : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    private string _name = "Widget";
    public string Name { get => _name; set => Set(ref _name, value); }

    private bool _visible = true;
    public bool Visible { get => _visible; set => Set(ref _visible, value); }

    private WidgetMode _mode = WidgetMode.Rings;
    public WidgetMode Mode { get => _mode; set => Set(ref _mode, value); }

    private WidgetPlacement _placement = WidgetPlacement.Desktop;
    public WidgetPlacement Placement { get => _placement; set => Set(ref _placement, value); }

    private double? _left;
    public double? Left { get => _left; set => Set(ref _left, value); }

    private double? _top;
    public double? Top { get => _top; set => Set(ref _top, value); }

    private double _opacity = 0.9;
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0.35, 1.0)); }

    private double _scale = 1.0;
    public double Scale { get => _scale; set => Set(ref _scale, Math.Clamp(value, 0.75, 1.6)); }

    private bool _locked;
    public bool Locked { get => _locked; set => Set(ref _locked, value); }

    private bool _clickThrough;
    public bool ClickThrough { get => _clickThrough; set => Set(ref _clickThrough, value); }

    private bool _showLoad = true;
    public bool ShowLoad { get => _showLoad; set => Set(ref _showLoad, value); }

    private bool _showRam = true;
    public bool ShowRam { get => _showRam; set => Set(ref _showRam, value); }

    private bool _showNetwork = true;
    public bool ShowNetwork { get => _showNetwork; set => Set(ref _showNetwork, value); }

    private bool _showFans = true;
    public bool ShowFans { get => _showFans; set => Set(ref _showFans, value); }

    private bool _showDisks = true;
    public bool ShowDisks { get => _showDisks; set => Set(ref _showDisks, value); }

    private bool _showFps = true;
    public bool ShowFps { get => _showFps; set => Set(ref _showFps, value); }
}

/// <summary>Kullanıcının bir sensöre koyduğu eşik (ham birimde: °C, RPM, %...).</summary>
public sealed class AlertRule
{
    public string SensorId { get; set; } = "";
    public AlertDirection Direction { get; set; } = AlertDirection.Above;
    public double Threshold { get; set; }
}

/// <summary>Fan profili: her kontrol edilebilir fan için ayrı eğri/mod.</summary>
public sealed class FanProfile
{
    public string Name { get; set; } = "";
    public Dictionary<string, FanCurveSettings> Curves { get; set; } = [];
}

/// <summary>Kalıcı kullanıcı ayarları. Her değişiklik kısa bir gecikmeyle diske yazılır.</summary>
public sealed class AppSettings : ObservableObject
{
    public static readonly string[] ProfileNames = ["Sessiz", "Normal", "Oyun"];
    public const int MaxWidgets = 4;

    // ================= Widget'lar =================
    private ObservableCollection<WidgetConfig> _widgets = [];
    public ObservableCollection<WidgetConfig> Widgets
    {
        get => _widgets;
        set
        {
            _widgets.CollectionChanged -= OnWidgetsChanged;
            foreach (var w in _widgets) w.PropertyChanged -= OnWidgetChanged;
            _widgets = value ?? [];
            _widgets.CollectionChanged += OnWidgetsChanged;
            foreach (var w in _widgets) w.PropertyChanged += OnWidgetChanged;
            OnPropertyChanged();
        }
    }

    private void OnWidgetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (WidgetConfig w in e.OldItems) w.PropertyChanged -= OnWidgetChanged;
        if (e.NewItems is not null) foreach (WidgetConfig w in e.NewItems) w.PropertyChanged += OnWidgetChanged;
        OnPropertyChanged(WidgetItemChanged);
    }

    private void OnWidgetChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(WidgetItemChanged);

    /// <summary>Bir widget'ın ayarı değişince yayınlanan özellik adı (kaydetmeyi tetikler).</summary>
    public const string WidgetItemChanged = "Widgets[]";

    public WidgetConfig AddWidget()
    {
        var w = new WidgetConfig { Name = $"Widget {_widgets.Count + 1}", Mode = Core.WidgetMode.Strip, Placement = WidgetPlacement.GameTopmost, ShowFans = false, ShowDisks = false };
        _widgets.Add(w);
        return w;
    }

    private bool _hideIdleFans = true;
    public bool HideIdleFans { get => _hideIdleFans; set => Set(ref _hideIdleFans, value); }

    private List<string> _pinnedSensorIds = [];
    /// <summary>Widget halkalarında gösterilecek sıcaklık sensörleri. Boşsa otomatik seçilir.</summary>
    public List<string> PinnedSensorIds { get => _pinnedSensorIds; set => Set(ref _pinnedSensorIds, value ?? []); }

    private List<string> _widgetExcludedIds = [];
    public List<string> WidgetExcludedIds { get => _widgetExcludedIds; set => Set(ref _widgetExcludedIds, value ?? []); }

    private List<string> _widgetExtraIds = [];
    public List<string> WidgetExtraIds { get => _widgetExtraIds; set => Set(ref _widgetExtraIds, value ?? []); }

    // ================= Sensör yönetimi =================
    private Dictionary<string, string> _sensorNames = [];
    public Dictionary<string, string> SensorNames { get => _sensorNames; set => Set(ref _sensorNames, value ?? []); }

    private List<string> _hiddenSensorIds = [];
    public List<string> HiddenSensorIds { get => _hiddenSensorIds; set => Set(ref _hiddenSensorIds, value ?? []); }

    // ================= Fan profilleri =================
    private List<FanProfile> _fanProfiles = [];
    public List<FanProfile> FanProfiles { get => _fanProfiles; set => Set(ref _fanProfiles, value ?? []); }

    private string _activeFanProfile = "Normal";
    /// <summary>Elle seçilen profil (oyun algılanınca geçici olarak "Oyun" kullanılır).</summary>
    public string ActiveFanProfile { get => _activeFanProfile; set => Set(ref _activeFanProfile, value); }

    public FanProfile Profile(string name)
    {
        var p = _fanProfiles.FirstOrDefault(x => x.Name == name);
        if (p is null)
        {
            p = new FanProfile { Name = name };
            _fanProfiles.Add(p);
        }
        return p;
    }

    public void SetFanCurve(string profile, string controlId, FanCurveSettings curve)
    {
        Profile(profile).Curves[controlId] = curve;
        OnPropertyChanged(nameof(FanProfiles));
    }

    // ================= Oyun =================
    private bool _fpsEnabled = true;
    public bool FpsEnabled { get => _fpsEnabled; set => Set(ref _fpsEnabled, value); }

    private bool _autoDetectGames = true;
    /// <summary>Tam ekran çalışan uygulamaları oyun say.</summary>
    public bool AutoDetectGames { get => _autoDetectGames; set => Set(ref _autoDetectGames, value); }

    private bool _gameSwitchesFanProfile = true;
    public bool GameSwitchesFanProfile { get => _gameSwitchesFanProfile; set => Set(ref _gameSwitchesFanProfile, value); }

    private List<string> _games = [];
    /// <summary>Oyun sayılan programlar (küçük harf, .exe'siz: "cyberpunk2077").</summary>
    public List<string> Games { get => _games; set => Set(ref _games, value ?? []); }

    public void AddGame(string processName)
    {
        var name = processName.Trim().ToLowerInvariant();
        if (name.EndsWith(".exe")) name = name[..^4];
        if (name.Length == 0 || _games.Contains(name)) return;
        Games = [.. _games, name];
    }

    public void RemoveGame(string name) => Games = _games.Where(g => g != name).ToList();

    // ================= Ölçüm =================
    private int _refreshMs = 1000;
    public int RefreshMs { get => _refreshMs; set => Set(ref _refreshMs, Math.Clamp(value, 250, 10_000)); }

    private bool _useFahrenheit;
    public bool UseFahrenheit { get => _useFahrenheit; set => Set(ref _useFahrenheit, value); }

    // ================= Uyarılar =================
    private double _cpuWarn = 85;
    public double CpuWarn { get => _cpuWarn; set => Set(ref _cpuWarn, Math.Round(Math.Clamp(value, 50, 105))); }

    private double _gpuWarn = 83;
    public double GpuWarn { get => _gpuWarn; set => Set(ref _gpuWarn, Math.Round(Math.Clamp(value, 50, 105))); }

    private double _diskWarn = 55;
    public double DiskWarn { get => _diskWarn; set => Set(ref _diskWarn, Math.Round(Math.Clamp(value, 35, 85))); }

    private bool _alertsEnabled = true;
    public bool AlertsEnabled { get => _alertsEnabled; set => Set(ref _alertsEnabled, value); }

    private bool _alertSound = true;
    public bool AlertSound { get => _alertSound; set => Set(ref _alertSound, value); }

    private bool _alertFanStop = true;
    public bool AlertFanStop { get => _alertFanStop; set => Set(ref _alertFanStop, value); }

    private bool _alertDiskFull = true;
    public bool AlertDiskFull { get => _alertDiskFull; set => Set(ref _alertDiskFull, value); }

    private double _diskFullPercent = 90;
    public double DiskFullPercent { get => _diskFullPercent; set => Set(ref _diskFullPercent, Math.Round(Math.Clamp(value, 70, 99))); }

    private List<AlertRule> _alertRules = [];
    public List<AlertRule> AlertRules { get => _alertRules; set => Set(ref _alertRules, value ?? []); }

    public void SetAlertRule(string sensorId, AlertRule? rule)
    {
        var list = _alertRules.Where(r => r.SensorId != sensorId).ToList();
        if (rule is not null) list.Add(rule);
        AlertRules = list;
    }

    // ================= Geçmiş =================
    private bool _historyEnabled = true;
    public bool HistoryEnabled { get => _historyEnabled; set => Set(ref _historyEnabled, value); }

    // ================= Görünüm =================
    private AppTheme _theme = AppTheme.Dark;
    public AppTheme Theme { get => _theme; set => Set(ref _theme, value); }

    private string _accent = "Ember";
    public string Accent { get => _accent; set => Set(ref _accent, value); }

    // ================= Telefondan izleme =================
    private bool _remoteEnabled;
    public bool RemoteEnabled { get => _remoteEnabled; set => Set(ref _remoteEnabled, value); }

    private int _remotePort = 8765;
    public int RemotePort { get => _remotePort; set => Set(ref _remotePort, Math.Clamp(value, 1024, 65000)); }

    private string _remoteToken = "";
    public string RemoteToken { get => _remoteToken; set => Set(ref _remoteToken, value); }

    public void NewRemoteToken() => RemoteToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    // ================= Sistem =================
    private bool _trayShowsTemp = true;
    public bool TrayShowsTemp { get => _trayShowsTemp; set => Set(ref _trayShowsTemp, value); }

    private bool _checkUpdates = true;
    /// <summary>Açılışta GitHub'da yeni sürüm var mı diye bak (günde bir kez).</summary>
    public bool CheckUpdates { get => _checkUpdates; set => Set(ref _checkUpdates, value); }

    private DateTime? _lastUpdateCheck;
    public DateTime? LastUpdateCheck { get => _lastUpdateCheck; set => Set(ref _lastUpdateCheck, value); }

    private bool _alwaysElevate = true;
    public bool AlwaysElevate { get => _alwaysElevate; set => Set(ref _alwaysElevate, value); }

    // ================= Yardımcılar =================
    public bool IsPinned(string id) => _pinnedSensorIds.Contains(id);

    /// <param name="seed">Liste boşken (otomatik mod) başlangıç olarak kullanılacak sensörler.</param>
    public void TogglePin(string id, int maxPins, IEnumerable<string> seed)
    {
        var list = _pinnedSensorIds.Count == 0 ? seed.ToList() : new List<string>(_pinnedSensorIds);
        if (!list.Remove(id))
        {
            if (list.Count >= maxPins) list.RemoveAt(0);
            list.Add(id);
        }
        PinnedSensorIds = list;
    }

    public void ToggleExcluded(string id) => WidgetExcludedIds = Toggle(_widgetExcludedIds, id);
    public void ToggleExtra(string id) => WidgetExtraIds = Toggle(_widgetExtraIds, id);
    public void ToggleHidden(string id) => HiddenSensorIds = Toggle(_hiddenSensorIds, id);

    public void SetSensorName(string id, string? name)
    {
        var copy = new Dictionary<string, string>(_sensorNames);
        if (string.IsNullOrWhiteSpace(name)) copy.Remove(id);
        else copy[id] = name.Trim();
        SensorNames = copy;
    }

    private static List<string> Toggle(List<string> list, string id)
    {
        var copy = new List<string>(list);
        if (!copy.Remove(id)) copy.Add(id);
        return copy;
    }

    // ================= Eski sürümden okunan alanlar (yazılmaz) =================
    // 0.1/0.2 tek widget'lı ayarları; Migrate() bunları Widgets[0] ve "Normal" profiline taşır.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public double? WidgetLeft { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public double? WidgetTop { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public bool? WidgetVisible { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public WidgetPlacement? Placement { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public double? WidgetOpacity { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public double? WidgetScale { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public bool? LockPosition { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public bool? ClickThrough { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public WidgetMode? WidgetMode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public bool? WidgetShowLoad { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public bool? WidgetShowRam { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public bool? WidgetShowNetwork { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public bool? ShowFans { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public bool? ShowDisks { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public Dictionary<string, FanCurveSettings>? FanCurves { get; set; }

    /// <summary>Eski ayar dosyasını yeni yapıya taşır ve eksik varsayılanları doldurur.</summary>
    public void Migrate()
    {
        if (_widgets.Count == 0)
        {
            Widgets = [new WidgetConfig
            {
                Name = "Widget 1",
                Visible = WidgetVisible ?? true,
                Mode = WidgetMode ?? Core.WidgetMode.Rings,
                Placement = Placement ?? WidgetPlacement.Desktop,
                Left = WidgetLeft,
                Top = WidgetTop,
                Opacity = WidgetOpacity ?? 0.9,
                Scale = WidgetScale ?? 1.0,
                Locked = LockPosition ?? false,
                ClickThrough = ClickThrough ?? false,
                ShowLoad = WidgetShowLoad ?? true,
                ShowRam = WidgetShowRam ?? true,
                ShowNetwork = WidgetShowNetwork ?? true,
                ShowFans = ShowFans ?? true,
                ShowDisks = ShowDisks ?? true,
            }];
        }

        foreach (var name in ProfileNames)
        {
            var p = Profile(name);
            if (name == "Normal" && FanCurves is { Count: > 0 } legacy && p.Curves.Count == 0)
                p.Curves = new Dictionary<string, FanCurveSettings>(legacy);
        }
        if (!ProfileNames.Contains(_activeFanProfile)) _activeFanProfile = "Normal";
        if (string.IsNullOrEmpty(_remoteToken)) NewRemoteToken();

        WidgetLeft = WidgetTop = null;
        FanCurves = null;
    }
}

/// <summary>%AppData%\Kor\settings.json okur/yazar.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kor");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private readonly DispatcherTimer _saveTimer;

    public AppSettings Settings { get; }
    public bool IsFirstRun { get; }

    private SettingsStore(AppSettings settings, bool firstRun)
    {
        Settings = settings;
        IsFirstRun = firstRun;
        Settings.Migrate();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        Settings.PropertyChanged += (_, _) => { _saveTimer.Stop(); _saveTimer.Start(); };
    }

    public static SettingsStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (s is not null) return new SettingsStore(s, firstRun: false);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Ayarlar okunamadı, varsayılanlar kullanılıyor", ex);
        }
        return new SettingsStore(new AppSettings(), firstRun: true);
    }

    public void SaveNow()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write("Ayarlar kaydedilemedi", ex);
        }
    }
}
