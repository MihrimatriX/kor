using System.Diagnostics;
using LibreHardwareMonitor.Hardware;

namespace Kor.Core;

public enum HwKind { Cpu, Gpu, Motherboard, Memory, Storage, Cooler, Psu, Network, Battery, Other, Game }

/// <summary>Bir sensörün tek anlık değeri (UI thread'ine taşınabilir, değişmez).</summary>
/// <param name="Unit">Doluysa standart birim yerine bu kullanılır (örn. FPS, ms).</param>
public sealed record SensorSample(string Id, string Name, SensorType Type, int Index, float? Value, float? Min, float? Max, bool Controllable = false, string? Unit = null);

public enum DiskHealthStatus { Unknown, Good, Caution, Bad }

/// <summary>SMART özeti (CrystalDiskInfo mantığıyla hesaplanan durum + ömür, çalışma süresi...).</summary>
public sealed record DiskHealthInfo(
    DiskHealthStatus Status,
    string Type,
    double? Life,
    double? PowerOnHours,
    double? PowerOnCount,
    double? HostWritesGb,
    double? TempWarning,
    double? TempCritical);

public sealed record HardwareSample(
    string Id,
    string Name,
    HwKind Kind,
    HardwareType RawType,
    string? ParentName,
    IReadOnlyList<SensorSample> Sensors,
    DiskHealthInfo? Health = null);

/// <summary>Tüm donanımın bir ölçüm turu. <see cref="Signature"/> sensör kümesi değişince değişir.</summary>
public sealed record HardwareSnapshot(IReadOnlyList<HardwareSample> Hardware, int Signature, DateTime Timestamp);

/// <summary>
/// LibreHardwareMonitor'u tek bir arka plan thread'inde çalıştırır (kütüphane thread-safe değil).
/// Her turda değişmez bir <see cref="HardwareSnapshot"/> üretir; UI bunu Dispatcher ile alır.
/// </summary>
public sealed class SensorService : ISensorSource
{
    // Kategoriler Open()'dan sonra tek tek açılır (bkz. OpenComputer).
    private readonly Computer _computer = new();

    // Fan kontrolü: kurallar UI'dan değişmez liste olarak gelir, sadece bu thread uygular.
    private readonly FanController _fans = new();
    private readonly Dictionary<string, ISensor> _sensorMap = [];
    private volatile IReadOnlyList<FanControlRule> _fanPlan = [];

    private readonly CancellationTokenSource _cts = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile int _intervalMs;

    /// <summary>Arka plan thread'inde tetiklenir.</summary>
    public event Action<HardwareSnapshot>? SnapshotReady;

    public SensorService(int intervalMs) => _intervalMs = intervalMs;

    public int IntervalMs
    {
        get => _intervalMs;
        set { _intervalMs = value; _wake.Set(); }
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Kor.Sensors", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>
    /// Önce boş açıp kategorileri sırayla etkinleştiriyoruz: bir sürücü/cihaz hata verirse
    /// sadece o kategori eksik kalır, diğer sensörler yine okunur.
    /// (CPU önce açılmalı: Intel iGPU grubu CPU listesine ihtiyaç duyuyor.)
    /// </summary>
    private void OpenComputer()
    {
        try
        {
            _computer.Open();
        }
        catch (Exception ex)
        {
            Log.Write("LibreHardwareMonitor açılamadı", ex);
            return;
        }

        (string Name, Action Enable)[] categories =
        [
            ("CPU", () => _computer.IsCpuEnabled = true),
            ("GPU", () => _computer.IsGpuEnabled = true),
            ("Anakart", () => _computer.IsMotherboardEnabled = true),
            ("Bellek", () => _computer.IsMemoryEnabled = true),
            ("Depolama", () => _computer.IsStorageEnabled = true),
            ("Fan kontrolcüleri", () => _computer.IsControllerEnabled = true), // Corsair/NZXT/Aquacomputer
            ("PSU", () => _computer.IsPsuEnabled = true),
            ("Ağ", () => _computer.IsNetworkEnabled = true),
        ];

        foreach (var (name, enable) in categories)
        {
            try
            {
                enable();
            }
            catch (Exception ex)
            {
                // Kısmen eklenen gruplar (örn. NVIDIA eklenip Intel iGPU patlarsa) çalışmaya devam eder.
                Log.Write($"{name} sensörleri başlatılamadı", ex);
            }
        }
    }

    private void Run()
    {
        OpenComputer();

        var token = _cts.Token;
        var handles = new[] { token.WaitHandle, _wake };

        while (!token.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                foreach (var hw in _computer.Hardware) UpdateRecursive(hw);
                var snapshot = Capture();
                ApplyFans();
                SnapshotReady?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                Log.Write("Sensör okuma turu başarısız", ex);
            }

            var wait = TimeSpan.FromMilliseconds(_intervalMs) - Stopwatch.GetElapsedTime(started);
            if (wait < TimeSpan.FromMilliseconds(100)) wait = TimeSpan.FromMilliseconds(100);
            WaitHandle.WaitAny(handles, wait);
        }

        // Çıkışta fanları mutlaka BIOS / sürücü kontrolüne geri ver.
        _fans.RestoreAll(FindControl);

        try { _computer.Close(); }
        catch (Exception ex) { Log.Write("LibreHardwareMonitor kapatılamadı", ex); }
    }

    public void SetFanPlan(IReadOnlyList<FanControlRule> plan)
    {
        _fanPlan = plan;
        _wake.Set();
    }

    private void ApplyFans()
    {
        try
        {
            _fans.Apply(_fanPlan, id => _sensorMap.TryGetValue(id, out var s) ? s.Value : null, FindControl);
        }
        catch (Exception ex)
        {
            Log.Write("Fan kontrolü uygulanamadı", ex);
        }
    }

    private IFanControl? FindControl(string id)
        => _sensorMap.TryGetValue(id, out var s) && s.Control is { } c ? new LhmFanControl(c) : null;

    private sealed class LhmFanControl(IControl control) : IFanControl
    {
        public float Min => control.MinSoftwareValue;
        public float Max => control.MaxSoftwareValue;
        public void SetSoftware(float percent) => control.SetSoftware(percent);
        public void SetDefault() => control.SetDefault();
    }

    private static void UpdateRecursive(IHardware hardware)
    {
        try
        {
            hardware.Update();
        }
        catch (Exception ex)
        {
            // Tek bir cihazın hatası diğerlerini durdurmasın.
            Debug.WriteLine($"{hardware.Name}: {ex.Message}");
        }

        foreach (var sub in hardware.SubHardware) UpdateRecursive(sub);
    }

    private HardwareSnapshot Capture()
    {
        var list = new List<HardwareSample>();
        var hash = new HashCode();
        _sensorMap.Clear();

        foreach (var hw in _computer.Hardware) Add(hw, null);

        return new HardwareSnapshot(list, hash.ToHashCode(), DateTime.Now);

        void Add(IHardware hw, IHardware? parent)
        {
            var sensors = new List<SensorSample>(hw.Sensors.Length);
            foreach (var s in hw.Sensors)
            {
                if (s.IsDefaultHidden) continue;
                // Kütüphanenin 24 saatlik dahili geçmişini kapat; geçmişi kendimiz tutuyoruz.
                if (s.ValuesTimeWindow != TimeSpan.Zero) s.ValuesTimeWindow = TimeSpan.Zero;

                var id = s.Identifier.ToString();
                hash.Add(id);
                _sensorMap[id] = s;
                sensors.Add(new SensorSample(id, s.Name, s.SensorType, s.Index, s.Value, s.Min, s.Max,
                    Controllable: s.SensorType == SensorType.Control && s.Control is not null));
            }

            var hwId = hw.Identifier.ToString();
            hash.Add(hwId);
            var health = hw.HardwareType == HardwareType.Storage ? CachedHealth(hw, hwId) : null;
            list.Add(new HardwareSample(hwId, hw.Name.Trim(), MapKind(hw.HardwareType), hw.HardwareType, parent?.Name.Trim(), sensors, health));

            foreach (var sub in hw.SubHardware) Add(sub, hw);
        }
    }

    private readonly Dictionary<string, (DiskHealthInfo? Info, long At)> _health = [];

    /// <summary>
    /// SMART özeti dakikalar içinde değişmez: 30 sn'de bir okunur, arada son değer kullanılır.
    /// Okunamadıysa (henüz hazır değil / geçici hata) 5 sn sonra yeniden denenir.
    /// </summary>
    private DiskHealthInfo? CachedHealth(IHardware hw, string id)
    {
        var now = Environment.TickCount64;
        if (_health.TryGetValue(id, out var cached) && now - cached.At < (cached.Info is null ? 5_000 : 30_000)) return cached.Info;
        var info = ReadHealth(hw);
        _health[id] = (info, now);
        return info;
    }

    /// <summary>LibreHardwareMonitor'un disk nesnesindeki SMART özetini okur (DiskInfoToolkit).</summary>
    private static DiskHealthInfo? ReadHealth(IHardware hw)
    {
        try
        {
            if (hw is not LibreHardwareMonitor.Hardware.Storage.StorageDevice { Storage: { } storage }) return null;
            var smart = storage.Smart;
            if (smart is null) return null;

            var status = smart.DiskStatus switch
            {
                DiskInfoToolkit.Enums.Interop.DiskStatus.Good => DiskHealthStatus.Good,
                DiskInfoToolkit.Enums.Interop.DiskStatus.Caution => DiskHealthStatus.Caution,
                DiskInfoToolkit.Enums.Interop.DiskStatus.Bad => DiskHealthStatus.Bad,
                _ => DiskHealthStatus.Unknown,
            };
            var hours = smart.MeasuredPowerOnHours > 0 ? smart.MeasuredPowerOnHours : smart.DetectedPowerOnHours;
            return new DiskHealthInfo(
                status,
                storage.IsNVMe ? "NVMe" : storage.IsSSD ? "SSD" : "HDD",
                ToDouble(smart.Life),
                hours > 0 ? hours : null,
                smart.PowerOnCount > 0 ? smart.PowerOnCount : null,
                ToDouble(smart.HostWrites),
                ToDouble(smart.TemperatureWarning),
                ToDouble(smart.TemperatureCritical));
        }
        catch
        {
            return null;
        }

        static double? ToDouble(object? value) => value is null ? null : Convert.ToDouble(value);
    }

    public static HwKind MapKind(HardwareType type) => type switch
    {
        HardwareType.Cpu => HwKind.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => HwKind.Gpu,
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => HwKind.Motherboard,
        HardwareType.Memory => HwKind.Memory,
        HardwareType.Storage => HwKind.Storage,
        HardwareType.Cooler => HwKind.Cooler,
        HardwareType.Psu => HwKind.Psu,
        HardwareType.Network => HwKind.Network,
        HardwareType.Battery => HwKind.Battery,
        _ => HwKind.Other,
    };

    private int _disposed;

    /// <summary>Thread'i durdurur; thread çıkarken fanları varsayılana döndürür. Birden çok kez çağrılabilir.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(3));
    }
}
