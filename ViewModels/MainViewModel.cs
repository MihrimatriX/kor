using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Media;
using Kor.Controls;
using Kor.Core;
using Kor.Infrastructure;
using LibreHardwareMonitor.Hardware;
using static Kor.Infrastructure.Paint;

namespace Kor.ViewModels;

/// <summary>
/// Uygulamanın ana ViewModel'i. Widget'lar (<see cref="WidgetViewModel"/>) ve detay penceresi bu örneği paylaşır.
/// Sensör kümesi değişmedikçe koleksiyonlar yeniden kurulmaz; sadece <see cref="SensorItem"/>'lar güncellenir.
/// Dosyalar: çekirdek (bu dosya), .Widgets (widget içerikleri), .Alerts (uyarılar), .Features (profil, oyun, disk sağlığı, uzaktan izleme, tema).
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const int MaxPins = 4;

    private static readonly Brush CpuLine = Frozen(0xFF, 0x7A, 0x45);
    private static readonly Brush GpuLine = Frozen(0x7D, 0xD3, 0xFC);
    private static readonly Brush DiskLine = Frozen(0x34, 0xD3, 0x99);

    private readonly Dictionary<string, SensorItem> _items = [];
    private readonly List<SensorItem> _allFans = [];
    private readonly List<(SensorItem Temp, string HardwareName)> _alertTargets = [];
    private readonly List<(SensorItem Temp, string Name)> _diskTemps = [];
    private readonly List<HardwareSample> _networkHw = [];
    private readonly HistoryRecorder? _recorder;
    private Dictionary<string, List<MinuteValue>> _pendingHistory = [];

    private int _signature;
    private int _tick;
    private HardwareSnapshot? _snapshot;

    private SensorItem? _cpuTemp, _cpuLoad, _gpuTemp, _gpuLoad;
    private SensorItem? _ramLoad, _ramUsed, _ramFree;
    private SensorItem? _netDown, _netUp;
    private SensorItem? _fps, _fpsLow;
    private string _cpuName = "", _gpuName = "";
    private (SensorItem Temp, string Name)? _heroDisk;

    public MainViewModel(AppSettings settings, bool isAdmin, bool pawnIoInstalled, HistoryRecorder? recorder)
    {
        Settings = settings;
        IsAdmin = isAdmin;
        IsPawnIoInstalled = pawnIoInstalled;
        _pawnIoAtStartup = pawnIoInstalled;
        _recorder = recorder;
        Settings.PropertyChanged += OnSettingChanged;

        NavigateCommand = new RelayCommand(p => { if (p is Page page) CurrentPage = page; });
        SelectSensorCommand = new RelayCommand(p => { if (p is SensorItem s) SelectedSensor = s; });
        ToggleWidgetCommand = new RelayCommand(p => { if ((p as SensorItem ?? SelectedSensor) is { } s) ToggleWidget(s); });
        ToggleHiddenCommand = new RelayCommand(p => { if ((p as SensorItem ?? SelectedSensor) is { } s) Settings.ToggleHidden(s.Id); });
        ResetNameCommand = new RelayCommand(() => { if (SelectedSensor is { } s) Settings.SetSensorName(s.Id, null); });
        ExportSelectedCommand = new RelayCommand(ExportSelected);
        ExportAllCommand = new RelayCommand(ExportAll);
        ClearPinsCommand = new RelayCommand(() =>
        {
            Settings.PinnedSensorIds = [];
            Settings.WidgetExcludedIds = [];
            Settings.WidgetExtraIds = [];
        });
        RestartAsAdminCommand = new RelayCommand(() => App.Current.RestartElevated());
        RestartCommand = new RelayCommand(() => App.Current.Restart());
        InstallPawnIoCommand = new RelayCommand(Elevation.InstallPawnIoWithWinget);
        OpenPawnIoSiteCommand = new RelayCommand(() => Elevation.OpenUrl("https://pawnio.eu"));
        OpenDataFolderCommand = new RelayCommand(() => Elevation.OpenUrl(SettingsStore.Folder));
        OpenLhmSiteCommand = new RelayCommand(() => Elevation.OpenUrl("https://github.com/LibreHardwareMonitor/LibreHardwareMonitor"));

        InitWidgets();
        InitAlerts();
        InitFeatures();

        _ = LoadStartupStateAsync();
    }

    public AppSettings Settings { get; }

    // ---- Genel bakış ----
    public ObservableCollection<HardwareCard> Overview { get; } = [];
    public ObservableCollection<DiskCard> Disks { get; } = [];
    public ObservableCollection<LegendItem> OverviewLegend { get; } = [];

    private IReadOnlyList<ChartSeries> _overviewSeries = [];
    public IReadOnlyList<ChartSeries> OverviewSeries { get => _overviewSeries; private set => Set(ref _overviewSeries, value); }

    private readonly DateTime _sessionStart = DateTime.Now;
    private string _sessionText = "";
    public string SessionText { get => _sessionText; private set => Set(ref _sessionText, value); }

    // ---- Sensörler ----
    public ObservableCollection<HardwareGroup> SensorGroups { get; } = [];

    private SensorFilter _filter = SensorFilter.Temperatures;
    public SensorFilter Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value)) return;
            OnPropertyChanged(nameof(ShowDiskHealth));
            RebuildSensorGroups();
        }
    }

    private bool _showHidden;
    public bool ShowHidden { get => _showHidden; set { if (Set(ref _showHidden, value)) RebuildSensorGroups(); } }

    public int HiddenCount => Settings.HiddenSensorIds.Count;

    private SensorItem? _selectedSensor;
    public SensorItem? SelectedSensor
    {
        get => _selectedSensor;
        set
        {
            var old = _selectedSensor;
            if (!Set(ref _selectedSensor, value)) return;
            if (old is not null) old.IsSelected = false;
            if (value is not null) value.IsSelected = true;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedName));
            RefreshAlertEditor();
            UpdateCharts();
        }
    }
    public bool HasSelection => _selectedSensor is not null;

    /// <summary>Detay panelindeki ad kutusu: yazınca sensör yeniden adlandırılır.</summary>
    public string SelectedName
    {
        get => _selectedSensor?.Name ?? "";
        set
        {
            if (_selectedSensor is not { } s) return;
            var trimmed = value?.Trim() ?? "";
            Settings.SetSensorName(s.Id, trimmed.Length == 0 || trimmed == s.OriginalName ? null : trimmed);
        }
    }

    private IReadOnlyList<ChartSeries> _selectedSeries = [];
    public IReadOnlyList<ChartSeries> SelectedSeries { get => _selectedSeries; private set => Set(ref _selectedSeries, value); }

    private string _statMin = "—", _statAvg = "—", _statMax = "—", _statSession = "—";
    public string StatMin { get => _statMin; private set => Set(ref _statMin, value); }
    public string StatAvg { get => _statAvg; private set => Set(ref _statAvg, value); }
    public string StatMax { get => _statMax; private set => Set(ref _statMax, value); }
    public string StatSession { get => _statSession; private set => Set(ref _statSession, value); }

    private double _selectedThreshold = double.NaN;
    public double SelectedThreshold { get => _selectedThreshold; private set => Set(ref _selectedThreshold, value); }

    // ---- Grafik aralığı (genel bakış ve sensör detayı ortak) ----
    private ChartRange _chartRange = ChartRange.FiveMinutes;
    public ChartRange ChartRange
    {
        get => _chartRange;
        set
        {
            if (!Set(ref _chartRange, value)) return;
            OnPropertyChanged(nameof(ChartSpan));
            UpdateCharts();
        }
    }
    public TimeSpan ChartSpan => _chartRange switch
    {
        ChartRange.FiveMinutes => TimeSpan.FromMinutes(5),
        ChartRange.Day => TimeSpan.FromHours(24),
        _ => TimeSpan.FromHours(1),
    };

    private bool _chartsActive;
    /// <summary>Detay penceresi ekranda (açık ve simge durumunda değil) iken true; değilse grafik verisi hazırlanmaz.</summary>
    public bool ChartsActive
    {
        get => _chartsActive;
        set
        {
            if (_chartsActive == value) return;
            _chartsActive = value;
            if (value) UpdateCharts(); // gizliyken bekleyen grafiği hemen tazele
        }
    }

    // ---- Fan kontrolü ----
    public ObservableCollection<FanControlItem> FanControls { get; } = [];
    public bool HasFanControls => FanControls.Count > 0;
    public IReadOnlyList<SensorItem> TemperatureSources => _items.Values.Where(i => i.IsTemperature && !i.IsHidden)
        .OrderBy(i => i.Kind).ThenBy(i => i.HardwareName).ThenBy(i => i.Index).ToList();

    public event Action<IReadOnlyList<FanControlRule>>? FanPlanChanged;

    // ---- Diğer ----
    public SensorItem? CpuTemp => _cpuTemp;
    public SensorItem? GpuTemp => _gpuTemp;
    public SensorItem? HeroDiskTemp => _heroDisk?.Temp;
    public SensorItem? Fps => _fps;

    public string MachineName { get; } = Environment.MachineName;
    public string AppVersion { get; } = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.3.0");

    public event Action? Updated;

    private bool _isLoading = true;
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    public bool IsAdmin { get; }

    private readonly bool _pawnIoAtStartup;
    private bool _isPawnIoInstalled;
    public bool IsPawnIoInstalled { get => _isPawnIoInstalled; private set => Set(ref _isPawnIoInstalled, value); }

    private string? _driverHint;
    public string? DriverHint { get => _driverHint; private set { if (Set(ref _driverHint, value)) OnPropertyChanged(nameof(HasDriverHint)); } }
    public bool HasDriverHint => _driverHint is not null;

    private bool _showInstallPawnIo;
    public bool ShowInstallPawnIo { get => _showInstallPawnIo; private set => Set(ref _showInstallPawnIo, value); }

    private bool _showRestart;
    public bool ShowRestart { get => _showRestart; private set => Set(ref _showRestart, value); }

    public bool ShowRestartAsAdmin => !IsAdmin;

    private string _lastUpdateText = "Sensörler okunuyor…";
    public string LastUpdateText { get => _lastUpdateText; private set => Set(ref _lastUpdateText, value); }

    private int _sensorCount;
    public int SensorCount { get => _sensorCount; private set => Set(ref _sensorCount, value); }

    private Page _currentPage = Page.Overview;
    public Page CurrentPage
    {
        get => _currentPage;
        set
        {
            if (!Set(ref _currentPage, value)) return;
            OnPropertyChanged(nameof(PageTitle));
            UpdateCharts(); // grafikler sadece görünen sayfa için hazırlanıyor
        }
    }

    public string PageTitle => _currentPage switch
    {
        Page.Overview => "Genel bakış",
        Page.Sensors => "Sensörler",
        Page.Fans => "Fan kontrolü",
        Page.Settings => "Ayarlar",
        _ => "",
    };

    private bool _startWithWindows;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value) return;
            _ = SetStartWithWindowsAsync(value);
        }
    }

    private string? _startupError;
    public string? StartupError { get => _startupError; private set => Set(ref _startupError, value); }

    private string? _exportMessage;
    public string? ExportMessage { get => _exportMessage; private set => Set(ref _exportMessage, value); }

    // ---- Komutlar ----
    public ICommand NavigateCommand { get; }
    public ICommand SelectSensorCommand { get; }
    public ICommand ToggleWidgetCommand { get; }
    public ICommand ToggleHiddenCommand { get; }
    public ICommand ResetNameCommand { get; }
    public ICommand ExportSelectedCommand { get; }
    public ICommand ExportAllCommand { get; }
    public ICommand ClearPinsCommand { get; }
    public ICommand RestartAsAdminCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand InstallPawnIoCommand { get; }
    public ICommand OpenPawnIoSiteCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLhmSiteCommand { get; }

    // =====================================================================
    //  Ölçüm
    // =====================================================================

    public void Apply(HardwareSnapshot snapshot)
    {
        List<(string Id, float Avg)>? completed = null; // dakikada bir dolar
        DateTime? completedMinute = null;

        foreach (var hw in snapshot.Hardware)
        {
            foreach (var s in hw.Sensors)
            {
                if (!_items.TryGetValue(s.Id, out var item))
                {
                    item = CreateItem(s, hw);
                    _items[s.Id] = item;
                }
                if (item.Apply(s, snapshot.Timestamp) is { } minute)
                {
                    (completed ??= new(_items.Count)).Add((s.Id, minute.Avg));
                    completedMinute = minute.Minute;
                }
            }
        }

        if (completedMinute is { } m && completed is not null && Settings.HistoryEnabled) _recorder?.Append(m, completed);

        _snapshot = snapshot;
        if (snapshot.Signature != _signature)
        {
            _signature = snapshot.Signature;
            RebuildStructure(snapshot);
        }

        _tick++;
        if (_tick % 30 == 0)
        {
            MaybeSwitchHeroDisk();
            if (PickNetwork()) InvalidateWidgets();
        }
        if (_tick % 15 == 0 && !IsPawnIoInstalled) IsPawnIoInstalled = Elevation.IsPawnIoInstalled();

        UpdateDiskHealth(snapshot);
        RebuildWidgetsIfNeeded();
        foreach (var w in Widgets)
        {
            if (w.Config.Visible) w.Refresh(); // gizli widget'ın penceresi yok
        }
        foreach (var fc in FanControls) fc.Tick();

        CheckAlerts();
        IsLoading = false;
        UpdateDriverHint();
        LastUpdateText = $"Son ölçüm {snapshot.Timestamp:HH:mm:ss}";
        UpdateSession();
        UpdateGameText();
        UpdateCharts();
        if (RemoteWatched) UpdateRemoteJson();

        Updated?.Invoke();
    }

    private SensorItem CreateItem(SensorSample s, HardwareSample hw)
    {
        var item = new SensorItem(s, hw, Settings) { IsHidden = Settings.HiddenSensorIds.Contains(s.Id) };
        if (_pendingHistory.Remove(s.Id, out var past)) item.History.Prefill(past);
        return item;
    }

    public SensorItem? Item(string id) => _items.GetValueOrDefault(id);

    /// <summary>Diskten okunan son 24 saatlik geçmişi sensörlere yükler (açılışta, arka plandan gelir).</summary>
    public void PrefillHistory(Dictionary<string, List<MinuteValue>> data)
    {
        foreach (var (id, list) in data)
        {
            if (_items.TryGetValue(id, out var item)) item.History.Prefill(list);
            else _pendingHistory[id] = list;
        }
        UpdateCharts();
    }

    private void RebuildStructure(HardwareSnapshot snapshot)
    {
        var live = snapshot.Hardware.SelectMany(h => h.Sensors).Select(s => s.Id).ToHashSet();
        foreach (var dead in _items.Keys.Where(k => !live.Contains(k)).ToList()) _items.Remove(dead);
        SensorCount = _items.Count;

        var ordered = snapshot.Hardware
            .Where(h => h.Sensors.Count > 0)
            .OrderBy(h => h.Kind)
            .ThenBy(h => h.RawType == HardwareType.GpuIntel ? 1 : 0) // harici GPU önce
            .ToList();

        Overview.Clear();
        Disks.Clear();
        _allFans.Clear();
        _alertTargets.Clear();
        _diskTemps.Clear();
        _networkHw.Clear();
        _cpuTemp = _cpuLoad = _gpuTemp = _gpuLoad = _ramLoad = _ramUsed = _ramFree = _fps = _fpsLow = null;
        var healthByDisk = new Dictionary<string, DiskHealthItem>();

        foreach (var hw in ordered)
        {
            var items = Items(hw);
            var visible = items.Where(i => !i.IsHidden).ToList();
            var temps = visible.Where(i => i.IsTemperature).ToList();
            var primary = PickPrimaryTemp(hw.Kind, temps);

            _allFans.AddRange(visible.Where(i => i.Type == SensorType.Fan));

            switch (hw.Kind)
            {
                case HwKind.Cpu when _cpuTemp is null && primary is not null:
                    _cpuTemp = primary;
                    _cpuLoad = Find(visible, SensorType.Load, "CPU Total");
                    _cpuName = Formatting.ShortHardwareName(hw.Name);
                    _alertTargets.Add((primary, hw.Name));
                    break;
                case HwKind.Gpu when _gpuTemp is null && primary is not null:
                    _gpuTemp = primary;
                    _gpuLoad = Find(visible, SensorType.Load, "GPU Core");
                    _gpuName = Formatting.ShortHardwareName(hw.Name);
                    _alertTargets.Add((primary, hw.Name));
                    break;
                case HwKind.Memory when _ramLoad is null && !hw.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase):
                    _ramLoad = Find(visible, SensorType.Load, "Memory");
                    _ramUsed = Find(visible, SensorType.Data, "Memory Used");
                    _ramFree = Find(visible, SensorType.Data, "Memory Available");
                    break;
                case HwKind.Network:
                    _networkHw.Add(hw);
                    break;
                case HwKind.Game:
                    _fps = items.FirstOrDefault(i => i.Id == GameSensors.FpsId);
                    _fpsLow = items.FirstOrDefault(i => i.Id == GameSensors.LowId);
                    break;
            }

            if (hw.Kind == HwKind.Storage)
            {
                var health = DiskHealthFor(hw, visible, primary);
                healthByDisk[hw.Id] = health;
                var disk = BuildDisk(hw, visible, primary, health);
                Disks.Add(disk);
                if (primary is not null)
                {
                    _diskTemps.Add((primary, disk.ShortName));
                    _alertTargets.Add((primary, hw.Name));
                }
            }
        }

        SyncDiskHealth(healthByDisk);
        PickNetwork();

        // Genel bakış kartları: sade tutmak için sadece ana donanımlar
        foreach (var hw in ordered)
        {
            var visible = Items(hw).Where(i => !i.IsHidden).ToList();
            var card = hw.Kind switch
            {
                HwKind.Network => hw.Id == _netDown?.HardwareId ? BuildNetworkCard(hw, visible) : null,
                HwKind.Memory => visible.Contains(_ramLoad!) ? BuildCard(hw, visible, _ramLoad, "Bellek") : null,
                HwKind.Game => null,
                _ => BuildCard(hw, visible, PickPrimaryTemp(hw.Kind, visible.Where(i => i.IsTemperature).ToList()), null),
            };
            if (card is not null && hw.Kind == HwKind.Storage) card = card with { Health = healthByDisk.GetValueOrDefault(hw.Id) };
            if (card is not null) Overview.Add(card);
        }

        _allFans.Sort((a, b) =>
        {
            var byKind = a.Kind.CompareTo(b.Kind);
            return byKind != 0 ? byKind : a.Index.CompareTo(b.Index);
        });

        _heroDisk = _diskTemps.Count > 0 ? _diskTemps.MaxBy(d => d.Temp.Value ?? 0) : null;

        RebuildSensorGroups();
        RebuildFanControls();
        InvalidateWidgets();
        RebuildWidgetsIfNeeded();
        RefreshAlertEditor();

        OnPropertyChanged(nameof(CpuTemp));
        OnPropertyChanged(nameof(GpuTemp));
        OnPropertyChanged(nameof(HeroDiskTemp));
        OnPropertyChanged(nameof(Fps));
        OnPropertyChanged(nameof(TemperatureSources));
    }

    private List<SensorItem> Items(HardwareSample hw)
        => hw.Sensors.Select(s => _items.GetValueOrDefault(s.Id)).OfType<SensorItem>().ToList();

    /// <summary>Birden çok ağ bağdaştırıcısı varsa en çok veri aktaranı seçer (sanal bağdaştırıcılar elenir).</summary>
    private bool PickNetwork()
    {
        HardwareSample? best = null;
        double bestTotal = -1;
        foreach (var hw in _networkHw)
        {
            var items = Items(hw);
            var total = (Find(items, SensorType.Data, "Data Downloaded")?.Value ?? 0) + (Find(items, SensorType.Data, "Data Uploaded")?.Value ?? 0);
            if (total > bestTotal) { best = hw; bestTotal = total; }
        }

        var down = best is null ? null : Find(Items(best), SensorType.Throughput, "Download Speed");
        var up = best is null ? null : Find(Items(best), SensorType.Throughput, "Upload Speed");
        if (down == _netDown && up == _netUp) return false;
        _netDown = down;
        _netUp = up;
        return true;
    }

    // =====================================================================
    //  Sensörler sayfası
    // =====================================================================

    private void RebuildSensorGroups()
    {
        SensorGroups.Clear();
        if (_snapshot is null) return;

        foreach (var hw in _snapshot.Hardware.Where(h => h.Sensors.Count > 0).OrderBy(h => h.Kind).ThenBy(h => h.RawType == HardwareType.GpuIntel ? 1 : 0))
        {
            var items = Items(hw).Where(i => _showHidden || !i.IsHidden).ToList();
            var subtitle = hw.ParentName is null ? Formatting.KindTitle(hw.Kind) : $"{Formatting.KindTitle(hw.Kind)} · {hw.ParentName}";
            List<SensorSection> sections = _filter switch
            {
                SensorFilter.Temperatures => [new SensorSection("", items.Where(i => i.IsTemperature).ToList())],
                SensorFilter.Fans =>
                [
                    new SensorSection("Hız", items.Where(i => i.Type == SensorType.Fan).ToList()),
                    new SensorSection("Kontrol", items.Where(i => i.Type == SensorType.Control).ToList()),
                ],
                SensorFilter.Disks when hw.Kind != HwKind.Storage => [],
                _ when hw.Kind == HwKind.Game => [new SensorSection("", items)],
                _ => items.GroupBy(i => i.Type)
                          .OrderBy(g => Formatting.TypeOrder(g.Key))
                          .Select(g => new SensorSection(Formatting.TypeTitle(g.Key), g.ToList()))
                          .ToList(),
            };
            sections = sections.Where(s => s.Items.Count > 0).ToList();
            if (_filter == SensorFilter.Fans && sections.Count == 1) sections = [sections[0] with { Title = "" }];
            if (sections.Count > 0) SensorGroups.Add(new HardwareGroup(hw.Id, hw.Name, subtitle, hw.Kind, sections));
        }
        OnPropertyChanged(nameof(HiddenCount));
    }

    // =====================================================================
    //  Grafikler ve oturum
    // =====================================================================

    /// <summary>Sadece ekranda görünen grafiğin verisini hazırlar (genel bakış ya da seçili sensör).</summary>
    private void UpdateCharts()
    {
        if (!_chartsActive) return;
        var now = DateTime.Now;
        var span = ChartSpan;

        if (_currentPage == Page.Sensors && _selectedSensor is { } s)
        {
            var points = s.History.GetPoints(span, now);
            var color = s.IsTemperature ? CpuLine : s.Accent;
            SelectedSeries = [new ChartSeries(s.Name, color, points, s.FormatValue)];
            if (points.Count > 0)
            {
                double min = double.MaxValue, max = double.MinValue, sum = 0;
                foreach (var p in points)
                {
                    min = Math.Min(min, p.Value);
                    max = Math.Max(max, p.Value);
                    sum += p.Value;
                }
                StatMin = s.FormatValue(min);
                StatAvg = s.FormatValue(sum / points.Count);
                StatMax = s.FormatValue(max);
            }
            else
            {
                StatMin = StatAvg = StatMax = "—";
            }
            StatSession = s.SessionMax is float sessionMax ? s.FormatValue(sessionMax) : "—";
            SelectedThreshold = RuleFor(s.Id) is { } rule
                ? rule.Threshold
                : s.IsTemperature && s.Kind is HwKind.Cpu or HwKind.Gpu or HwKind.Storage
                    ? TempScale.Thresholds(s.Kind, Settings).Warn
                    : double.NaN;
        }
        else if (_selectedSensor is null && SelectedSeries.Count > 0)
        {
            SelectedSeries = [];
        }

        if (_currentPage != Page.Overview) return;
        var series = new List<ChartSeries>(3);
        void AddLine(string name, Brush brush, SensorItem? item)
        {
            if (item is not null) series.Add(new ChartSeries(name, brush, item.History.GetPoints(span, now), item.FormatValue));
        }
        AddLine("CPU", CpuLine, _cpuTemp);
        AddLine("GPU", GpuLine, _gpuTemp);
        AddLine("Disk", DiskLine, _heroDisk?.Temp);
        OverviewSeries = series;

        if (OverviewLegend.Count != series.Count)
        {
            OverviewLegend.Clear();
            if (_cpuTemp is not null) OverviewLegend.Add(new LegendItem("CPU", CpuLine, _cpuTemp));
            if (_gpuTemp is not null) OverviewLegend.Add(new LegendItem("GPU", GpuLine, _gpuTemp));
            if (_heroDisk is { } d) OverviewLegend.Add(new LegendItem("Disk", DiskLine, d.Temp));
        }
    }

    private void UpdateSession()
    {
        var elapsed = DateTime.Now - _sessionStart;
        var duration = elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours} sa {elapsed.Minutes} dk" : $"{Math.Max(1, elapsed.Minutes)} dk";
        var parts = new List<string>();
        void High(string name, SensorItem? item)
        {
            if (item?.SessionMax is float max) parts.Add($"{name} {item.FormatValue(max)}");
        }
        High("CPU", _cpuTemp);
        High("GPU", _gpuTemp);
        High("Disk", _heroDisk?.Temp);
        SessionText = parts.Count > 0 ? $"Oturum {duration} · en yüksek: {string.Join(" · ", parts)}" : $"Oturum {duration}";
    }

    // =====================================================================
    //  Dışa aktarma
    // =====================================================================

    private void ExportSelected()
    {
        if (_selectedSensor is not { } s) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Kor_{Sanitize(s.Name)}_{DateTime.Now:yyyyMMdd_HHmm}.csv",
            Filter = "CSV dosyası (*.csv)|*.csv",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var points = s.History.GetPoints(ChartSpan, DateTime.Now);
            CsvExport.Write(dialog.FileName, [$"{s.Name} ({s.Unit})".Replace(" ()", "")],
                points.Select(p => (p.Time, (IReadOnlyList<float?>)[p.Value])));
            ExportMessage = $"Kaydedildi: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            ExportMessage = "Kaydedilemedi: " + ex.Message;
        }
    }

    private void ExportAll()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Kor_gecmis_{DateTime.Now:yyyyMMdd_HHmm}.csv",
            Filter = "CSV dosyası (*.csv)|*.csv",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var since = DateTime.Now.AddHours(-24);
            var items = _items.Values.Where(i => !i.IsHidden).OrderBy(i => i.Kind).ThenBy(i => i.HardwareName).ThenBy(i => Formatting.TypeOrder(i.Type)).ToList();
            var byMinute = new SortedDictionary<DateTime, float?[]>();
            for (var c = 0; c < items.Count; c++)
            {
                foreach (var m in items[c].History.Minutes(since))
                {
                    if (!byMinute.TryGetValue(m.Minute, out var row)) byMinute[m.Minute] = row = new float?[items.Count];
                    row[c] = m.Avg;
                }
            }
            var headers = items.Select(i => i.Unit.Length > 0 ? $"{i.HardwareName} / {i.Name} ({i.Unit})" : $"{i.HardwareName} / {i.Name}").ToList();
            CsvExport.Write(dialog.FileName, headers, byMinute.Select(kv => (kv.Key, (IReadOnlyList<float?>)kv.Value)));
            ExportMessage = byMinute.Count == 0
                ? "Henüz dakikalık veri yok (ilk dakika dolduktan sonra tekrar dene)."
                : $"Kaydedildi: {Path.GetFileName(dialog.FileName)} ({byMinute.Count} dakika)";
        }
        catch (Exception ex)
        {
            ExportMessage = "Kaydedilemedi: " + ex.Message;
        }
    }

    private static string Sanitize(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '_' : ch));

    // =====================================================================
    //  Sürücü ipucu, ayarlar
    // =====================================================================

    private void UpdateDriverHint()
    {
        if (!IsAdmin)
        {
            DriverHint = "Yönetici izni yok: CPU sıcaklığı, anakart fanları ve disk sıcaklıkları okunamaz.";
            ShowInstallPawnIo = false;
            ShowRestart = false;
        }
        else if (!IsPawnIoInstalled)
        {
            DriverHint = "PawnIO sürücüsü kurulu değil: CPU sıcaklığı ve anakart fanları için gerekli.";
            ShowInstallPawnIo = true;
            ShowRestart = false;
        }
        else if (!_pawnIoAtStartup)
        {
            DriverHint = "PawnIO kuruldu. Yeni sensörler için Kor'u yeniden başlat.";
            ShowInstallPawnIo = false;
            ShowRestart = true;
        }
        else if (_cpuTemp?.Value is null)
        {
            DriverHint = "CPU sıcaklığı okunamadı. İşlemcin LibreHardwareMonitor tarafından desteklenmiyor olabilir.";
            ShowInstallPawnIo = false;
            ShowRestart = false;
        }
        else
        {
            DriverHint = null;
            ShowInstallPawnIo = false;
            ShowRestart = false;
        }
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.UseFahrenheit):
            case nameof(AppSettings.CpuWarn):
            case nameof(AppSettings.GpuWarn):
            case nameof(AppSettings.DiskWarn):
                foreach (var item in _items.Values) item.Refresh();
                foreach (var w in Widgets) w.Refresh();
                RefreshAlertEditor();
                UpdateCharts();
                UpdateSession();
                Updated?.Invoke();
                break;

            case nameof(AppSettings.SensorNames):
                foreach (var item in _items.Values) item.ApplyName(Settings.SensorNames.GetValueOrDefault(item.Id));
                foreach (var fc in FanControls) fc.RefreshNames();
                OnPropertyChanged(nameof(SelectedName));
                OnPropertyChanged(nameof(TemperatureSources));
                InvalidateWidgets();
                RebuildWidgetsIfNeeded();
                UpdateCharts();
                break;

            case nameof(AppSettings.HiddenSensorIds):
                foreach (var item in _items.Values) item.IsHidden = Settings.HiddenSensorIds.Contains(item.Id);
                if (_snapshot is not null) RebuildStructure(_snapshot);
                break;

            case nameof(AppSettings.PinnedSensorIds):
            case nameof(AppSettings.WidgetExcludedIds):
            case nameof(AppSettings.WidgetExtraIds):
            case nameof(AppSettings.HideIdleFans):
            case AppSettings.WidgetItemChanged:
            case nameof(AppSettings.Widgets):
                RebuildWidgetsIfNeeded(force: true);
                Updated?.Invoke();
                break;

            case nameof(AppSettings.AlertRules):
                _ruledIds = null;
                RefreshAlertEditor();
                UpdateCharts();
                break;

            case nameof(AppSettings.ActiveFanProfile):
            case nameof(AppSettings.GameSwitchesFanProfile):
                OnEffectiveProfileMaybeChanged();
                break;

            case nameof(AppSettings.Games):
                OnPropertyChanged(nameof(GameList));
                break;

            case nameof(AppSettings.RemoteEnabled):
            case nameof(AppSettings.RemotePort):
            case nameof(AppSettings.RemoteToken):
                UpdateRemoteServer();
                break;

            case nameof(AppSettings.Theme):
            case nameof(AppSettings.Accent):
                RefreshAccentOptions();
                UpdateRemoteQr();
                break;
        }
    }

    // ---- Windows ile başlat ----

    private async Task LoadStartupStateAsync()
    {
        var enabled = await Task.Run(StartupTask.IsEnabled);
        _startWithWindows = enabled;
        OnPropertyChanged(nameof(StartWithWindows));
    }

    private async Task SetStartWithWindowsAsync(bool enable)
    {
        if (!IsAdmin)
        {
            StartupError = "Bu ayar için Kor'u yönetici olarak çalıştır.";
            OnPropertyChanged(nameof(StartWithWindows));
            return;
        }

        StartupError = null;
        var (ok, error) = await Task.Run(() =>
        {
            var success = enable ? StartupTask.Enable(out var err) : StartupTask.Disable(out err);
            return (success, err);
        });

        if (ok) _startWithWindows = enable;
        else StartupError = error ?? "Görev Zamanlayıcı işlemi başarısız.";
        OnPropertyChanged(nameof(StartWithWindows));
    }

    // =====================================================================
    //  Kart kurucuları
    // =====================================================================

    private static SensorItem? Find(List<SensorItem> items, SensorType type, params string[] names)
    {
        foreach (var n in names)
        {
            var exact = items.FirstOrDefault(i => i.Type == type && i.OriginalName.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }
        foreach (var n in names)
        {
            var partial = items.FirstOrDefault(i => i.Type == type && i.OriginalName.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (partial is not null) return partial;
        }
        return null;
    }

    private static SensorItem? PickPrimaryTemp(HwKind kind, List<SensorItem> temps)
    {
        if (temps.Count == 0) return null;
        string[] prefs = kind switch
        {
            HwKind.Cpu => ["CPU Package", "Core (Tctl/Tdie)", "Tctl/Tdie", "Tdie", "Tctl", "Package", "Core Average", "Core Max"],
            HwKind.Gpu => ["GPU Core", "GPU Temperature", "GPU Hot Spot"],
            HwKind.Storage => ["Temperature", "Composite Temperature"],
            HwKind.Motherboard => ["CPU", "System", "Motherboard"],
            _ => [],
        };
        return Find(temps, SensorType.Temperature, prefs) ?? temps[0];
    }

    private static DiskCard BuildDisk(HardwareSample hw, List<SensorItem> items, SensorItem? temp, DiskHealthItem health)
    {
        var stats = new List<StatItem>();
        void Add(string label, SensorItem? s) { if (s is not null) stats.Add(new StatItem(label, s)); }

        Add("Okuma", Find(items, SensorType.Throughput, "Read Rate", "Read"));
        Add("Yazma", Find(items, SensorType.Throughput, "Write Rate", "Write"));
        var remaining = Find(items, SensorType.Level, "Remaining Life", "Life");
        if (remaining is not null) Add("Kalan ömür", remaining);
        else Add("Ömür kullanımı", Find(items, SensorType.Level, "Percentage Used"));

        var name = hw.Name;
        var shortName = name.Length > 22 ? name[..21].TrimEnd() + "…" : name;
        return new DiskCard(name, shortName, hw.Id, temp,
            Find(items, SensorType.Load, "Used Space"),
            Find(items, SensorType.Load, "Total Activity", "Activity"),
            stats, health);
    }

    private static HardwareCard BuildNetworkCard(HardwareSample hw, List<SensorItem> items)
    {
        var stats = new List<StatItem>();
        void Add(string label, SensorItem? s) { if (s is not null) stats.Add(new StatItem(label, s)); }
        Add("İndirme", Find(items, SensorType.Throughput, "Download Speed"));
        Add("Yükleme", Find(items, SensorType.Throughput, "Upload Speed"));
        Add("İndirilen", Find(items, SensorType.Data, "Data Downloaded"));
        Add("Yüklenen", Find(items, SensorType.Data, "Data Uploaded"));
        return new HardwareCard(hw.Name, "Ağ", HwKind.Network, null, stats);
    }

    private static HardwareCard? BuildCard(HardwareSample hw, List<SensorItem> items, SensorItem? primary, string? titleOverride)
    {
        if (hw.Kind is HwKind.Battery or HwKind.Other or HwKind.Game) return null;

        var stats = new List<StatItem>();
        void Add(string label, SensorItem? s)
        {
            if (s is not null && s != primary && stats.Count < 4 && stats.All(x => x.Sensor != s))
                stats.Add(new StatItem(label, s));
        }

        switch (hw.Kind)
        {
            case HwKind.Cpu:
                Add("Yük", Find(items, SensorType.Load, "CPU Total"));
                Add("Güç", Find(items, SensorType.Power, "CPU Package", "Package"));
                Add("Saat", Find(items, SensorType.Clock, "Core #1", "Core"));
                Add("En sıcak çekirdek", Find(items, SensorType.Temperature, "Core Max"));
                break;
            case HwKind.Gpu:
                Add("Yük", Find(items, SensorType.Load, "GPU Core"));
                Add("Fan", items.FirstOrDefault(i => i.Type == SensorType.Fan));
                Add("Güç", Find(items, SensorType.Power, "GPU Package", "GPU Power", "GPU Core"));
                Add("VRAM", Find(items, SensorType.SmallData, "GPU Memory Used"));
                break;
            case HwKind.Memory:
                Add("Kullanılan", Find(items, SensorType.Data, "Memory Used", "Used"));
                Add("Boş", Find(items, SensorType.Data, "Memory Available", "Available"));
                break;
            case HwKind.Storage:
                Add("Doluluk", Find(items, SensorType.Load, "Used Space"));
                Add("Okuma", Find(items, SensorType.Throughput, "Read Rate", "Read"));
                Add("Yazma", Find(items, SensorType.Throughput, "Write Rate", "Write"));
                break;
        }

        foreach (var t in items.Where(i => i.Type == SensorType.Temperature)) Add(t.Name, t);
        foreach (var f in items.Where(i => i.Type == SensorType.Fan && !i.IsIdle)) Add(f.Name, f);

        if (primary is null && stats.Count == 0) return null;
        return new HardwareCard(titleOverride ?? hw.Name, Formatting.KindTitle(hw.Kind), hw.Kind, primary, stats);
    }

}
