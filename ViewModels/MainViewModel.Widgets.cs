using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using Kor.Core;
using Kor.Infrastructure;
using LibreHardwareMonitor.Hardware;

namespace Kor.ViewModels;

/// <summary>Widget içerikleri: hangi halka/satırın hangi widget'ta görüneceği.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Her <see cref="WidgetConfig"/> için bir içerik modeli (pencereler App'te açılır).</summary>
    public ObservableCollection<WidgetViewModel> Widgets { get; } = [];

    private WidgetConfig? _selectedWidget;
    /// <summary>Ayarlar sayfasında düzenlenen widget.</summary>
    public WidgetConfig? SelectedWidget { get => _selectedWidget; set { if (Set(ref _selectedWidget, value)) OnPropertyChanged(nameof(CanRemoveWidget)); } }

    public bool CanAddWidget => Settings.Widgets.Count < AppSettings.MaxWidgets;
    public bool CanRemoveWidget => Settings.Widgets.Count > 1;

    public ICommand SelectWidgetCommand { get; private set; } = null!;
    public ICommand AddWidgetCommand { get; private set; } = null!;
    public ICommand RemoveWidgetCommand { get; private set; } = null!;

    private void InitWidgets()
    {
        SelectWidgetCommand = new RelayCommand(p => { if (p is WidgetConfig w) SelectedWidget = w; });
        AddWidgetCommand = new RelayCommand(() =>
        {
            if (!CanAddWidget) return;
            SelectedWidget = Settings.AddWidget();
        });
        RemoveWidgetCommand = new RelayCommand(p =>
        {
            if ((p as WidgetConfig ?? SelectedWidget) is not { } w || !CanRemoveWidget) return;
            Settings.Widgets.Remove(w);
            SelectedWidget = Settings.Widgets.FirstOrDefault();
        });

        SyncWidgets();
        Settings.Widgets.CollectionChanged += OnWidgetConfigsChanged;
        SelectedWidget = Settings.Widgets.FirstOrDefault();
    }

    private void OnWidgetConfigsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncWidgets();
        RebuildWidgetsIfNeeded();
    }

    private void SyncWidgets()
    {
        var configs = Settings.Widgets;
        foreach (var dead in Widgets.Where(w => !configs.Contains(w.Config)).ToList()) Widgets.Remove(dead);
        foreach (var cfg in configs)
        {
            if (Widgets.All(w => w.Config != cfg)) Widgets.Add(new WidgetViewModel(this, cfg));
        }
        OnPropertyChanged(nameof(CanAddWidget));
        OnPropertyChanged(nameof(CanRemoveWidget));
    }

    public WidgetViewModel? WidgetFor(WidgetConfig config) => Widgets.FirstOrDefault(w => w.Config == config);

    // ---------------------------------------------------------------------

    private sealed record HeroSpec(string Label, string Caption, SensorItem Temp, SensorItem? Load);
    private sealed record RowSpec(WidgetRowKind Kind, string Label, SensorItem Primary, SensorItem? Secondary = null, SensorItem? Tertiary = null);

    private bool IsExcluded(string key) => Settings.WidgetExcludedIds.Contains(key);

    private List<HeroSpec> HeroSpecs(WidgetConfig cfg)
    {
        var pinned = Settings.PinnedSensorIds
            .Select(id => _items.GetValueOrDefault(id))
            .OfType<SensorItem>()
            .Where(i => i.IsTemperature && !i.IsHidden)
            .Take(MaxPins)
            .Select(p => new HeroSpec(Formatting.KindShort(p.Kind), p.Name, p, null))
            .ToList();
        if (pinned.Count > 0) return pinned;

        var auto = new List<HeroSpec>();
        var load = cfg.ShowLoad;
        if (_cpuTemp is not null) auto.Add(new HeroSpec("CPU", _cpuName, _cpuTemp, load && _cpuLoad is { } cl && !IsExcluded(cl.Id) ? cl : null));
        if (_gpuTemp is not null) auto.Add(new HeroSpec("GPU", _gpuName, _gpuTemp, load && _gpuLoad is { } gl && !IsExcluded(gl.Id) ? gl : null));
        if (_heroDisk is { } d && !d.Temp.IsHidden) auto.Add(new HeroSpec("DİSK", d.Name, d.Temp, null));
        return auto;
    }

    private List<RowSpec> LoadSpecs(WidgetConfig cfg)
    {
        var list = new List<RowSpec>();
        if (!cfg.ShowLoad) return list;
        if (_cpuLoad is { IsHidden: false } c && !IsExcluded(c.Id)) list.Add(new RowSpec(WidgetRowKind.Load, "CPU yükü", c));
        if (_gpuLoad is { IsHidden: false } g && !IsExcluded(g.Id)) list.Add(new RowSpec(WidgetRowKind.Load, "GPU yükü", g));
        return list;
    }

    private List<RowSpec> RowSpecs(WidgetConfig cfg)
    {
        var list = new List<RowSpec>();
        if (cfg.ShowRam && _ramLoad is { IsHidden: false } ram && !IsExcluded(ram.Id))
            list.Add(new RowSpec(WidgetRowKind.Memory, "RAM", ram, _ramUsed, _ramFree));

        if (cfg.ShowNetwork && _netDown is { IsHidden: false } down && !IsExcluded(down.Id))
            list.Add(new RowSpec(WidgetRowKind.Network, "Ağ", down, _netUp));

        if (cfg.ShowFans)
        {
            foreach (var fan in _allFans.Where(f => !f.IsHidden && !IsExcluded(f.Id) && !(Settings.HideIdleFans && f.IsIdle)).Take(8))
                list.Add(new RowSpec(WidgetRowKind.Fan, fan.Name, fan));
        }

        if (cfg.ShowDisks)
        {
            foreach (var disk in Disks.Where(d => !IsExcluded(d.HardwareId)))
            {
                var key = disk.Temp ?? disk.Used;
                if (key is not null) list.Add(new RowSpec(WidgetRowKind.Disk, disk.ShortName, key, disk.Used));
            }
        }

        foreach (var id in Settings.WidgetExtraIds)
        {
            if (_items.GetValueOrDefault(id) is { IsHidden: false } extra)
                list.Add(new RowSpec(extra.IsTemperature ? WidgetRowKind.Temperature : WidgetRowKind.Other, extra.Name, extra));
        }
        return list;
    }

    private void InvalidateWidgets()
    {
        foreach (var w in Widgets) w.Key = "";
    }

    /// <summary>Widget içeriği değiştiyse (fan dönmeye başladı, oyun açıldı, ad değişti, ayar...) yeniden kurar.</summary>
    private void RebuildWidgetsIfNeeded()
    {
        var pinnedMode = Settings.PinnedSensorIds.Count > 0;
        foreach (var w in Widgets)
        {
            var cfg = w.Config;
            var heroes = HeroSpecs(cfg);
            var loads = LoadSpecs(cfg);
            var rows = RowSpecs(cfg);
            var fpsActive = cfg.ShowFps && _fps?.Value is not null;
            var gameName = CurrentGame?.ProcessName ?? "Oyun";

            var key = string.Join('|', heroes.Select(h => $"{h.Temp.Id}:{h.Caption}:{h.Load?.Id}"))
                      + "#" + string.Join('|', loads.Select(r => r.Primary.Id))
                      + "#" + string.Join('|', rows.Select(r => $"{r.Kind}:{r.Primary.Id}:{r.Label}"))
                      + "#" + (fpsActive ? gameName : "");
            if (key == w.Key) continue;
            w.Key = key;

            var heroGauges = heroes.Select(h => new HeroGauge(h.Label, h.Caption, h.Temp, h.Load)).ToList();
            var heroRows = heroes.Select(h => new WidgetRow(WidgetRowKind.Temperature,
                pinnedMode ? h.Temp.Name : h.Label switch { "DİSK" => "Disk", var l => l }, h.Temp)).ToList();
            var loadRows = loads.Select(r => new WidgetRow(r.Kind, r.Label, r.Primary)).ToList();
            var otherRows = rows.Select(r => new WidgetRow(r.Kind, r.Label, r.Primary, r.Secondary, r.Tertiary)).ToList();
            var fpsRow = fpsActive ? new WidgetRow(WidgetRowKind.Fps, gameName, _fps!, _fpsLow) : null;

            var shown = new HashSet<string>(heroes.Select(h => h.Temp.Id));
            foreach (var h in heroes) if (h.Load is not null) shown.Add(h.Load.Id);
            foreach (var r in loads.Concat(rows))
            {
                shown.Add(r.Primary.Id);
                if (r.Secondary is not null) shown.Add(r.Secondary.Id);
                if (r.Tertiary is not null) shown.Add(r.Tertiary.Id);
            }
            if (fpsRow is not null)
            {
                shown.Add(_fps!.Id);
                if (_fpsLow is not null) shown.Add(_fpsLow.Id);
            }

            w.Rebuild(heroGauges, heroRows, loadRows, otherRows, fpsRow, shown);
        }

        // Sensörler sayfasındaki "widget'ta" işaretleri: görünür widget'ların birleşimi
        var union = new HashSet<string>();
        foreach (var w in Widgets.Where(w => w.Config.Visible)) union.UnionWith(w.Shown);
        foreach (var item in _items.Values) item.IsInWidget = union.Contains(item.Id);
    }

    /// <summary>
    /// Sensörler sayfasındaki "widget'ta göster" düğmesi (tüm widget'lar için ortak):
    ///  • Sıcaklık → halkaya sabitle / kaldır
    ///  • Varsayılan olarak görünen (fan, yük, RAM, ağ, disk) → widget'tan çıkar / geri ekle
    ///  • Diğerleri (güç, saat, voltaj...) → widget'a ekstra satır olarak ekle / kaldır
    /// </summary>
    public void ToggleWidget(SensorItem item)
    {
        if (item.Kind == HwKind.Game) return; // FPS satırı oyun varken kendiliğinden gelir

        if (item.IsTemperature && !Settings.WidgetExtraIds.Contains(item.Id))
        {
            var first = Settings.Widgets.FirstOrDefault() ?? new WidgetConfig();
            var seed = Settings.PinnedSensorIds.Count == 0 ? HeroSpecs(first).Select(h => h.Temp.Id) : [];
            Settings.TogglePin(item.Id, MaxPins, seed);
            return;
        }

        var exclusionKey = ExclusionKey(item);
        if (exclusionKey is not null) Settings.ToggleExcluded(exclusionKey);
        else Settings.ToggleExtra(item.Id);
    }

    private string? ExclusionKey(SensorItem item)
    {
        if (item == _cpuLoad || item == _gpuLoad) return item.Id;
        if (item == _ramLoad || item == _ramUsed || item == _ramFree) return _ramLoad?.Id;
        if (item == _netDown || item == _netUp) return _netDown?.Id;
        if (item.Type == SensorType.Fan) return item.Id;
        if (item.Kind == HwKind.Storage && Disks.FirstOrDefault(d => d.HardwareId == item.HardwareId) is { } disk && (item == disk.Used || item == disk.Temp))
            return disk.HardwareId;
        return null;
    }

    private void MaybeSwitchHeroDisk()
    {
        if (Settings.PinnedSensorIds.Count > 0 || _diskTemps.Count < 2 || _heroDisk is null) return;
        var hottest = _diskTemps.MaxBy(d => d.Temp.Value ?? 0);
        if (hottest.Temp != _heroDisk.Value.Temp && (hottest.Temp.Value ?? 0) > (_heroDisk.Value.Temp.Value ?? 0) + 2)
        {
            _heroDisk = hottest;
            InvalidateWidgets();
        }
    }
}
