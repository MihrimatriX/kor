using System.Collections.ObjectModel;
using Kor.Core;
using Kor.Infrastructure;

namespace Kor.ViewModels;

/// <summary>
/// Tek bir widget penceresinin içeriği. Her widget'ın modu, konumu ve hangi bölümleri gösterdiği
/// (<see cref="WidgetConfig"/>) ayrıdır; sensör nesneleri (<see cref="SensorItem"/>) ise ortaktır.
/// Unity karşılığı: aynı veri modeline bağlı birden çok UI Canvas prefab örneği.
/// </summary>
public sealed class WidgetViewModel(MainViewModel main, WidgetConfig config) : ObservableObject
{
    public MainViewModel Main { get; } = main;
    public WidgetConfig Config { get; } = config;

    public ObservableCollection<HeroGauge> Heroes { get; } = [];
    /// <summary>Halkaların liste/şerit karşılığı.</summary>
    public ObservableCollection<WidgetRow> HeroRows { get; } = [];
    public ObservableCollection<WidgetRow> LoadRows { get; } = [];
    /// <summary>RAM, ağ, fanlar, diskler, eklenen sensörler.</summary>
    public ObservableCollection<WidgetRow> WidgetRows { get; } = [];
    /// <summary>Şerit modu sade kalsın diye fan ve disk satırları olmadan.</summary>
    public ObservableCollection<WidgetRow> StripRows { get; } = [];

    private WidgetRow? _fpsRow;
    /// <summary>Oyun algılanıp FPS ölçülünce dolu; her modda en üstte gösterilir.</summary>
    public WidgetRow? FpsRow { get => _fpsRow; private set { if (Set(ref _fpsRow, value)) OnPropertyChanged(nameof(HasFps)); } }
    public bool HasFps => _fpsRow is not null;

    public bool HasHeroes => Heroes.Count > 0;
    public bool HasWidgetRows => WidgetRows.Count > 0;
    /// <summary>Halka modunda halkalarla satırlar arasındaki çizgi (halka yoksa gereksiz).</summary>
    public bool ShowRingDivider => HasHeroes && (WidgetRows.Count > 0 || (!LoadUnderRings && LoadRows.Count > 0));
    /// <summary>Halka modunda yük, otomatik halkaların altında yazar; sabitlenmiş halkalarda satır olarak gelir.</summary>
    public bool LoadUnderRings => Main.Settings.PinnedSensorIds.Count == 0;

    /// <summary>Son kurulan içeriğin anahtarı; değişmedikçe koleksiyonlar yeniden kurulmaz.</summary>
    internal string Key { get; set; } = "";

    /// <summary>Bu widget'ta görünen sensör kimlikleri (Sensörler sayfasındaki işaret için).</summary>
    internal HashSet<string> Shown { get; } = [];

    public void Refresh()
    {
        FpsRow?.Refresh();
        foreach (var row in HeroRows) row.Refresh();
        foreach (var row in LoadRows) row.Refresh();
        foreach (var row in WidgetRows) row.Refresh();
    }

    internal void Rebuild(IReadOnlyList<HeroGauge> heroes, IReadOnlyList<WidgetRow> heroRows, IReadOnlyList<WidgetRow> loads,
                          IReadOnlyList<WidgetRow> rows, WidgetRow? fps, IEnumerable<string> shown)
    {
        Heroes.Clear();
        foreach (var h in heroes) Heroes.Add(h);
        HeroRows.Clear();
        foreach (var r in heroRows) HeroRows.Add(r);
        LoadRows.Clear();
        foreach (var r in loads) LoadRows.Add(r);
        WidgetRows.Clear();
        StripRows.Clear();
        foreach (var r in rows)
        {
            WidgetRows.Add(r);
            if (r.Kind is not WidgetRowKind.Fan and not WidgetRowKind.Disk) StripRows.Add(r);
        }
        FpsRow = fps;

        Shown.Clear();
        Shown.UnionWith(shown);

        Refresh();
        OnPropertyChanged(nameof(HasHeroes));
        OnPropertyChanged(nameof(HasWidgetRows));
        OnPropertyChanged(nameof(LoadUnderRings));
        OnPropertyChanged(nameof(ShowRingDivider));
    }
}
