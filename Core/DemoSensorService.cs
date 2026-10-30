using LibreHardwareMonitor.Hardware;

namespace Kor.Core;

/// <summary>Sensör kaynağı soyutlaması: gerçek (LibreHardwareMonitor) veya demo.</summary>
public interface ISensorSource : IDisposable
{
    event Action<HardwareSnapshot>? SnapshotReady;
    int IntervalMs { get; set; }
    void Start();
    void SetFanPlan(IReadOnlyList<FanControlRule> plan);
}

/// <summary>
/// "--demo" argümanıyla açılır: donanım okumadan, gerçekçi sahte verilerle arayüzü gösterir.
/// Fan eğrileri de simüle edilir (kontrol yüzdesi → RPM).
/// </summary>
public sealed class DemoSensorService(int intervalMs) : ISensorSource
{
    private const string CpuFanCtl = "/lpc/nct6793d/0/control/0";
    private const string SysFanCtl = "/lpc/nct6793d/0/control/1";
    private const string GpuFanCtl = "/gpu-nvidia/0/control/0";

    private readonly Random _rng = new(42);
    private readonly Dictionary<string, (float Min, float Max)> _minMax = [];
    private readonly Dictionary<string, float> _values = [];
    private readonly Dictionary<string, DemoControl> _controls = new()
    {
        [CpuFanCtl] = new(),
        [SysFanCtl] = new(),
        [GpuFanCtl] = new() { MinValue = 25 },
    };
    private readonly FanController _fans = new();
    private volatile IReadOnlyList<FanControlRule> _plan = [];
    private Timer? _timer;
    private double _t;
    private double _downloaded = 182.4, _uploaded = 21.7;

    public event Action<HardwareSnapshot>? SnapshotReady;
    public int IntervalMs { get; set; } = intervalMs;

    public void Start() => _timer = new Timer(_ => Tick(), null, 300, Timeout.Infinite);

    public void SetFanPlan(IReadOnlyList<FanControlRule> plan) => _plan = plan;

    private void Tick()
    {
        try
        {
            TickCore();
        }
        catch (Exception ex)
        {
            Log.Write("Demo veri üretilemedi", ex);
        }
        _timer?.Change(IntervalMs, Timeout.Infinite);
    }

    private void TickCore()
    {
        var dt = IntervalMs / 1000.0;
        _t += dt;
        var load = Math.Clamp(0.35 + 0.3 * Math.Sin(_t / 9) + 0.15 * Math.Sin(_t / 2.3), 0, 1);
        var gpuLoad = Math.Clamp(0.5 + 0.45 * Math.Sin(_t / 14 + 1), 0, 1);
        var down = 1024 * 1024 * Math.Max(0, 3 + 6 * Math.Sin(_t / 6)) + 40_000;
        var up = 1024 * Math.Max(0, 80 + 300 * Math.Sin(_t / 4));
        _downloaded += down * dt / (1024d * 1024 * 1024);
        _uploaded += up * dt / (1024d * 1024 * 1024);

        // Fan kontrolü: kurallar uygulanır, yoksa "BIOS" eğrisi
        _fans.Apply(_plan, id => _values.TryGetValue(id, out var v) ? v : null, id => _controls.GetValueOrDefault(id));
        var cpuPct = _controls[CpuFanCtl].Value ?? (float)(35 + 50 * load);
        var sysPct = _controls[SysFanCtl].Value ?? (float)(30 + 20 * load);
        var gpuPct = _controls[GpuFanCtl].Value ?? (float)(30 + 55 * gpuLoad);

        var hw = new List<HardwareSample>
        {
            Hw("/intelcpu/0", "Intel Core i7-6700K", HwKind.Cpu, HardwareType.Cpu, null,
                S("/intelcpu/0/temperature/4", "CPU Package", SensorType.Temperature, 48 + 26 * load),
                S("/intelcpu/0/temperature/0", "Core #1", SensorType.Temperature, 46 + 25 * load),
                S("/intelcpu/0/temperature/1", "Core #2", SensorType.Temperature, 47 + 27 * load),
                S("/intelcpu/0/temperature/2", "Core #3", SensorType.Temperature, 45 + 24 * load),
                S("/intelcpu/0/temperature/3", "Core #4", SensorType.Temperature, 46 + 26 * load),
                S("/intelcpu/0/temperature/5", "Core Max", SensorType.Temperature, 49 + 28 * load),
                S("/intelcpu/0/load/0", "CPU Total", SensorType.Load, 100 * load),
                S("/intelcpu/0/clock/1", "Core #1", SensorType.Clock, 4000 + 200 * load),
                S("/intelcpu/0/power/0", "CPU Package", SensorType.Power, 18 + 70 * load)),

            Hw("/gpu-nvidia/0", "NVIDIA GeForce GTX 1080", HwKind.Gpu, HardwareType.GpuNvidia, null,
                S("/gpu-nvidia/0/temperature/0", "GPU Core", SensorType.Temperature, 42 + 32 * gpuLoad - (gpuPct - 50) * 0.08),
                S("/gpu-nvidia/0/temperature/2", "GPU Hot Spot", SensorType.Temperature, 50 + 36 * gpuLoad),
                S("/gpu-nvidia/0/load/0", "GPU Core", SensorType.Load, 100 * gpuLoad),
                S("/gpu-nvidia/0/fan/0", "GPU Fan", SensorType.Fan, 2800 * gpuPct / 100),
                S(GpuFanCtl, "GPU Fan", SensorType.Control, gpuPct, controllable: true),
                S("/gpu-nvidia/0/power/0", "GPU Package", SensorType.Power, 40 + 140 * gpuLoad),
                S("/gpu-nvidia/0/smalldata/1", "GPU Memory Used", SensorType.SmallData, 2300 + 3000 * gpuLoad),
                S("/gpu-nvidia/0/clock/0", "GPU Core", SensorType.Clock, 1600 + 250 * gpuLoad)),

            Hw("/lpc/nct6793d/0", "Nuvoton NCT6793D", HwKind.Motherboard, HardwareType.SuperIO, "ASUS Z170-A",
                S("/lpc/nct6793d/0/temperature/0", "CPU Core", SensorType.Temperature, 44 + 20 * load),
                S("/lpc/nct6793d/0/temperature/1", "Motherboard", SensorType.Temperature, 36 + 4 * load),
                S("/lpc/nct6793d/0/temperature/2", "Auxiliary", SensorType.Temperature, 31 + 2 * load),
                S("/lpc/nct6793d/0/fan/0", "Fan #1", SensorType.Fan, 1900 * cpuPct / 100),
                S("/lpc/nct6793d/0/fan/1", "Fan #2", SensorType.Fan, 1400 * sysPct / 100),
                S("/lpc/nct6793d/0/fan/2", "Fan #3", SensorType.Fan, 640 + 310 * load),
                S("/lpc/nct6793d/0/fan/3", "Fan #4", SensorType.Fan, 0),
                S(CpuFanCtl, "Fan Control #1", SensorType.Control, cpuPct, controllable: true),
                S(SysFanCtl, "Fan Control #2", SensorType.Control, sysPct, controllable: true),
                S("/lpc/nct6793d/0/voltage/0", "Vcore", SensorType.Voltage, 1.25 + 0.05 * load)),

            Hw("/ram", "Total Memory", HwKind.Memory, HardwareType.Memory, null,
                S("/ram/load/0", "Memory", SensorType.Load, 58 + 6 * load),
                S("/ram/data/0", "Memory Used", SensorType.Data, 9.4 + 1.1 * load),
                S("/ram/data/1", "Memory Available", SensorType.Data, 6.6 - 1.1 * load)),

            Hw("/vram", "Virtual Memory", HwKind.Memory, HardwareType.Memory, null,
                S("/vram/load/0", "Memory", SensorType.Load, 41 + 4 * load),
                S("/vram/data/0", "Memory Used", SensorType.Data, 13.1 + 1.2 * load),
                S("/vram/data/1", "Memory Available", SensorType.Data, 18.9 - 1.2 * load)),

            Hw("/nic/eth0", "Ethernet", HwKind.Network, HardwareType.Network, null,
                S("/nic/eth0/load/1", "Network Utilization", SensorType.Load, down * 8 / 1e9 * 100),
                S("/nic/eth0/data/2", "Data Uploaded", SensorType.Data, _uploaded),
                S("/nic/eth0/data/3", "Data Downloaded", SensorType.Data, _downloaded),
                S("/nic/eth0/throughput/7", "Upload Speed", SensorType.Throughput, up),
                S("/nic/eth0/throughput/8", "Download Speed", SensorType.Throughput, down)),

            Hw("/nic/veth", "vEthernet (Default Switch)", HwKind.Network, HardwareType.Network, null,
                S("/nic/veth/load/1", "Network Utilization", SensorType.Load, 0),
                S("/nic/veth/data/2", "Data Uploaded", SensorType.Data, 0.02),
                S("/nic/veth/data/3", "Data Downloaded", SensorType.Data, 0.05),
                S("/nic/veth/throughput/7", "Upload Speed", SensorType.Throughput, 0),
                S("/nic/veth/throughput/8", "Download Speed", SensorType.Throughput, 0)),

            Hw("/nvme/0", "Samsung SSD 970 EVO Plus 500GB", HwKind.Storage, HardwareType.Storage, null,
                S("/nvme/0/temperature/0", "Temperature", SensorType.Temperature, 39 + 8 * load),
                S("/nvme/0/load/0", "Used Space", SensorType.Load, 71.4),
                S("/nvme/0/load/3", "Total Activity", SensorType.Load, 6 + 20 * Math.Max(0, Math.Sin(_t / 3))),
                S("/nvme/0/throughput/0", "Read Rate", SensorType.Throughput, 1024 * 1024 * (2 + 40 * Math.Max(0, Math.Sin(_t / 3)))),
                S("/nvme/0/throughput/1", "Write Rate", SensorType.Throughput, 1024 * 1024 * (0.5 + 8 * Math.Max(0, Math.Sin(_t / 5)))),
                S("/nvme/0/level/0", "Percentage Used", SensorType.Level, 3),
                S("/nvme/0/data/1", "Data Written", SensorType.Data, 18_420)) with
            {
                Health = new DiskHealthInfo(DiskHealthStatus.Good, "NVMe", 97, 3_412, 1_206, 18_420, 70, 85),
            },

            Hw("/hdd/1", "WDC WD20EZRZ-00Z5HB0", HwKind.Storage, HardwareType.Storage, null,
                S("/hdd/1/temperature/0", "Temperature", SensorType.Temperature, 34 + 2 * load),
                S("/hdd/1/load/0", "Used Space", SensorType.Load, 88.2),
                S("/hdd/1/throughput/0", "Read Rate", SensorType.Throughput, 1024 * 300),
                S("/hdd/1/throughput/1", "Write Rate", SensorType.Throughput, 1024 * 40),
                S("/hdd/1/data/1", "Data Written", SensorType.Data, 9_870)) with
            {
                // Demo'da bir disk "Dikkat" gösterir (yeniden atanmış sektör senaryosu)
                Health = new DiskHealthInfo(DiskHealthStatus.Caution, "HDD", null, 21_870, 3_120, 9_870, 60, 70),
            },
        };

        var hash = new HashCode();
        foreach (var h in hw)
        {
            hash.Add(h.Id);
            foreach (var s in h.Sensors) hash.Add(s.Id);
        }

        SnapshotReady?.Invoke(new HardwareSnapshot(hw, hash.ToHashCode(), DateTime.Now));
    }

    private static HardwareSample Hw(string id, string name, HwKind kind, HardwareType type, string? parent, params SensorSample[] sensors)
        => new(id, name, kind, type, parent, sensors);

    private SensorSample S(string id, string name, SensorType type, double value, bool controllable = false)
    {
        var jitter = type switch
        {
            SensorType.Temperature or SensorType.Load => (_rng.NextDouble() - 0.5) * 1.2,
            SensorType.Fan => (_rng.NextDouble() - 0.5) * 30,
            _ => 0,
        };
        var v = (float)Math.Max(0, value == 0 ? 0 : value + jitter);
        _values[id] = v;
        var (min, max) = _minMax.TryGetValue(id, out var mm) ? (Math.Min(mm.Min, v), Math.Max(mm.Max, v)) : (v, v);
        _minMax[id] = (min, max);
        var index = int.TryParse(id[(id.LastIndexOf('/') + 1)..], out var i) ? i : 0;
        return new SensorSample(id, name, type, index, v, min, max, controllable);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _fans.RestoreAll(id => _controls.GetValueOrDefault(id));
    }

    private sealed class DemoControl : IFanControl
    {
        public float? Value { get; private set; }
        public float MinValue { get; init; }
        public float Min => MinValue;
        public float Max => 100;
        public void SetSoftware(float percent) => Value = percent;
        public void SetDefault() => Value = null;
    }
}
