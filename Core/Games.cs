using System.Diagnostics;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Kor.Core;

/// <summary>Algılanan oyun.</summary>
public sealed record GameInfo(string ProcessName, int ProcessId, bool FromList);

/// <summary>FPS ölçümü sonucu.</summary>
public sealed record FpsReading(string ProcessName, double Fps, double Low1, double FrameMs);

/// <summary>FPS kaynağı: gerçek (ETW) ya da demo.</summary>
public interface IFpsSource : IDisposable
{
    /// <summary>"Çalışıyor", "Yönetici izni gerekli" gibi durum metni.</summary>
    string Status { get; }
    bool IsRunning { get; }
    FpsReading? Read(GameInfo? game);

    /// <summary>Oyun başlayınca/bitince çağrılır; oyun yokken ölçüm kaynağı boşta bekleyebilir.</summary>
    void SetGameActive(bool active) { }
}

/// <summary>
/// Oyun algılama:
///  1) Kullanıcının listesindeki bir program çalışıyorsa o oyundur.
///  2) İsteğe bağlı: ön plandaki pencere monitörü tamamen kaplıyorsa (tam ekran) oyun sayılır.
/// </summary>
public static partial class GameDetector
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "kor", "applicationframehost", "textinputhost", "shellexperiencehost", "searchhost",
        "startmenuexperiencehost", "lockapp", "searchapp", "dwm", "taskmgr",
    };

    /// <param name="games">Büyük/küçük harf duyarsız (OrdinalIgnoreCase) süreç adı kümesi.</param>
    public static GameInfo? Detect(IReadOnlySet<string> games, bool autoFullscreen)
    {
        if (games.Count > 0)
        {
            // Tek sistem çağrısıyla tüm süreçlerin anlık görüntüsü; ad karşılaştırması ek çağrı yapmaz.
            GameInfo? found = null;
            foreach (var p in Process.GetProcesses())
            {
                if (found is null && games.Contains(p.ProcessName))
                    found = new GameInfo(p.ProcessName, p.Id, FromList: true);
                p.Dispose();
            }
            if (found is not null) return found;
        }

        return autoFullscreen ? FullscreenForeground() : null;
    }

    /// <summary>Ön plandaki pencerenin süreci (FPS ölçümü için).</summary>
    public static int ForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return 0;
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    private static GameInfo? FullscreenForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || hwnd == GetShellWindow() || hwnd == GetDesktopWindow()) return null;
        if (!GetWindowRect(hwnd, out var wr)) return null;

        var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;
        var m = info.rcMonitor;
        if (wr.Left > m.Left || wr.Top > m.Top || wr.Right < m.Right || wr.Bottom < m.Bottom) return null;

        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return Ignored.Contains(p.ProcessName) ? null : new GameInfo(p.ProcessName, p.Id, FromList: false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"Oyun ekle" menüsü için pencere başlığı olan programlar.</summary>
    public static IReadOnlyList<(string Process, string Title)> RunningApps()
    {
        var list = new List<(string, string)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle != 0 && !string.IsNullOrWhiteSpace(p.MainWindowTitle) && !Ignored.Contains(p.ProcessName))
                        list.Add((p.ProcessName, p.MainWindowTitle));
                }
                catch
                {
                    // Erişilemeyen süreçleri atla
                }
            }
        }
        return list.DistinctBy(x => x.Item1.ToLowerInvariant()).OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial nint GetShellWindow();
    [LibraryImport("user32.dll")] private static partial nint GetDesktopWindow();
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(nint hwnd, out RECT rect);
    [LibraryImport("user32.dll")] private static partial nint MonitorFromWindow(nint hwnd, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
}

/// <summary>
/// ETW ile FPS ölçümü (PresentMon'un yöntemi): DXGI (DX10/11/12) ve D3D9 "Present" olaylarını
/// süreç bazında sayar. Yönetici izni gerekir. Pencereli/kenarlıksız tam ekran oyunlarda widget üstte görünür;
/// özel (exclusive) tam ekranda ölçüm çalışır ama widget oyunun üstüne çıkamaz.
/// </summary>
public sealed class EtwFpsMonitor : IFpsSource
{
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int DxgiPresentStart = 42;
    private const int D3D9PresentStart = 1;

    private readonly Lock _gate = new();
    private readonly Dictionary<int, PresentLog> _presents = [];
    private readonly TraceEventSession? _session;
    private bool _providersOn;
    private (int Pid, string Name) _nameCache = (0, "");

    /// <summary>Bir sürecin son 5 sn'deki Present zamanları (ETW saatiyle, ms) ve son olayın geliş anı.</summary>
    private sealed class PresentLog
    {
        public readonly Queue<double> Times = new();
        public long LastArrival;
    }

    public string Status { get; private set; } = "Başlatılmadı";
    public bool IsRunning { get; private set; }

    public EtwFpsMonitor(bool isAdmin)
    {
        if (!isAdmin)
        {
            Status = "Yönetici izni gerekli";
            return;
        }

        try
        {
            // Oturum açık durur ama sağlayıcılar sadece oyun varken etkin (SetGameActive):
            // oyun yokken tarayıcı, Discord, masaüstü gibi her DXGI programının olayları hiç üretilmez.
            _session = new TraceEventSession("Kor-FPS") { StopOnDispose = true };
            _session.Source.AllEvents += OnEvent;
            var thread = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { Log.Write("FPS oturumu durdu", ex); IsRunning = false; Status = "Durdu"; }
            })
            { IsBackground = true, Name = "Kor.Fps" };
            thread.Start();
            IsRunning = true;
            Status = "Çalışıyor";
        }
        catch (Exception ex)
        {
            Log.Write("FPS ölçümü başlatılamadı", ex);
            Status = "Başlatılamadı: " + ex.Message;
            _session?.Dispose();
            _session = null;
        }
    }

    public void SetGameActive(bool active)
    {
        if (_session is null || !IsRunning || active == _providersOn) return;
        try
        {
            if (active)
            {
                // Sadece "Present" olayı (Windows 8.1+ çekirdek tarafında süzer; eski sürümde seçenek yok sayılır)
                _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue, new TraceEventProviderOptions { EventIDsToEnable = [DxgiPresentStart] });
                _session.EnableProvider(D3D9Provider, TraceEventLevel.Informational, ulong.MaxValue, new TraceEventProviderOptions { EventIDsToEnable = [D3D9PresentStart] });
            }
            else
            {
                _session.DisableProvider(DxgiProvider);
                _session.DisableProvider(D3D9Provider);
                lock (_gate) _presents.Clear();
            }
            _providersOn = active;
        }
        catch (Exception ex)
        {
            Log.Write(active ? "FPS sağlayıcıları açılamadı" : "FPS sağlayıcıları kapatılamadı", ex);
        }
    }

    private void OnEvent(TraceEvent e)
    {
        var id = (int)e.ID;
        if (!((id == DxgiPresentStart && e.ProviderGuid == DxgiProvider) || (id == D3D9PresentStart && e.ProviderGuid == D3D9Provider))) return;

        var t = e.TimeStampRelativeMSec;
        var arrival = Environment.TickCount64;
        lock (_gate)
        {
            if (!_presents.TryGetValue(e.ProcessID, out var log)) _presents[e.ProcessID] = log = new PresentLog();
            log.Times.Enqueue(t);
            log.LastArrival = arrival;
            while (log.Times.Count > 0 && t - log.Times.Peek() > 5000) log.Times.Dequeue();
        }
    }

    public FpsReading? Read(GameInfo? game)
    {
        // Sadece oyun algılanınca ölç: yoksa tarayıcı gibi DXGI kullanan her program "FPS" üretir.
        if (!IsRunning || game is null) return null;
        var pid = game.ProcessId;
        double[] times;
        var now = Environment.TickCount64;
        lock (_gate)
        {
            // Önce oyunun kendi süreci; çizimi ayrı bir alt süreç yapıyorsa ön plandaki pencere
            if (!_presents.TryGetValue(pid, out var log) || log.Times.Count < 2)
            {
                pid = GameDetector.ForegroundProcessId();
                if (!_presents.TryGetValue(pid, out log) || log.Times.Count < 2) return null;
            }
            // Artık kare üretmiyor (gerçek zamanlı ETW ~1 sn gecikmeli gelir, payı ona göre)
            if (now - log.LastArrival > 2500) return null;
            times = log.Times.ToArray();

            // Ölü süreçlerin kayıtlarını temizle
            List<int>? dead = null;
            foreach (var (key, value) in _presents)
                if (now - value.LastArrival > 10_000) (dead ??= []).Add(key);
            if (dead is not null) foreach (var key in dead) _presents.Remove(key);
        }

        return Compute(NameOf(pid, game), times);
    }

    /// <summary>
    /// Süreç adı. Process.ProcessName tüm süreçlerin listesini çeker; saniyede bir bunu yapmamak için
    /// oyunun kendi sürecinde algılamadaki adı, alt süreçte ise önbelleği kullanır.
    /// </summary>
    private string NameOf(int pid, GameInfo game)
    {
        if (pid == game.ProcessId) return game.ProcessName;
        if (_nameCache.Pid == pid) return _nameCache.Name;
        string name;
        try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
        catch { name = game.ProcessName; }
        _nameCache = (pid, name);
        return name;
    }

    /// <summary>Son 1 sn'deki kare sayısı = FPS; son 5 sn'deki en yavaş %1 kare = "1% düşük".</summary>
    public static FpsReading? Compute(string name, double[] times)
    {
        if (times.Length < 2) return null;
        var last = times[^1];
        var fps = times.Count(t => last - t < 1000);
        var deltas = new double[times.Length - 1];
        for (var i = 1; i < times.Length; i++) deltas[i - 1] = times[i] - times[i - 1];
        Array.Sort(deltas);
        var worstCount = Math.Max(1, deltas.Length / 100);
        var worst = deltas[^worstCount..].Average();
        var avg = deltas.Average();
        return new FpsReading(name, fps, worst > 0 ? 1000 / worst : 0, avg);
    }

    public void Dispose()
    {
        IsRunning = false;
        try { _session?.Dispose(); } catch { /* yoksay */ }
    }
}

/// <summary>--demo için: 120–144 FPS arası dalgalanan sahte oyun.</summary>
public sealed class DemoFpsSource : IFpsSource
{
    private readonly Random _rng = new(7);
    private readonly DateTime _start = DateTime.Now;
    public string Status => "Demo";
    public bool IsRunning => true;

    /// <summary>Demo oyun 90 sn açık, 30 sn kapalı döner.</summary>
    public GameInfo? DemoGame() => (DateTime.Now - _start).TotalSeconds % 120 < 90 ? new GameInfo("DemoOyun", 4242, true) : null;

    public FpsReading? Read(GameInfo? game)
    {
        if (game is null) return null;
        var t = (DateTime.Now - _start).TotalSeconds;
        var fps = 132 + 10 * Math.Sin(t / 4) + _rng.NextDouble() * 4;
        return new FpsReading(game.ProcessName, Math.Round(fps), Math.Round(fps * 0.72), 1000 / fps);
    }

    public void Dispose() { }
}

/// <summary>FPS ölçümünü bir "Oyun" donanımı gibi sensör listesine ekler (geçmiş, grafik, widget hepsi çalışsın).</summary>
public static class GameSensors
{
    public const string HardwareId = "/kor/game";
    public const string FpsId = "/kor/game/fps";
    public const string LowId = "/kor/game/low1";
    public const string FrameId = "/kor/game/frametime";

    private static readonly Dictionary<string, (float Min, float Max)> MinMax = [];

    public static HardwareSample Sample(FpsReading? r)
    {
        return new HardwareSample(HardwareId, "Oyun", HwKind.Game, HardwareType.EmbeddedController, null,
        [
            S(FpsId, "FPS", r?.Fps, "FPS"),
            S(LowId, "1% düşük", r?.Low1, "FPS"),
            S(FrameId, "Kare süresi", r?.FrameMs, "ms"),
        ]);
    }

    private static SensorSample S(string id, string name, double? value, string unit)
    {
        float? v = value is double d ? (float)d : null;
        if (v is float f)
        {
            var mm = MinMax.TryGetValue(id, out var x) ? (Math.Min(x.Min, f), Math.Max(x.Max, f)) : (f, f);
            MinMax[id] = mm;
        }
        var (min, max) = MinMax.TryGetValue(id, out var m) ? m : (0f, 0f);
        return new SensorSample(id, name, SensorType.Frequency, 0, v, v is null && min == 0 ? null : min, v is null && max == 0 ? null : max, Unit: unit);
    }
}
