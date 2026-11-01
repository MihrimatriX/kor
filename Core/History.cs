using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace Kor.Core;

public enum ChartRange { FiveMinutes, Hour, Day }

/// <summary>Zaman damgalı tek değer.</summary>
public readonly record struct TimedValue(DateTime Time, float Value);

/// <summary>Dakikalık ortalama (24 saatlik grafik ve diske kayıt için).</summary>
public readonly record struct MinuteValue(DateTime Minute, float Avg);

/// <summary>
/// Bir sensörün geçmişi:
///  • Son 1 saat, her ölçüm (saniye çözünürlüğü): en fazla 3600 kayıt
///  • Son 24 saat, dakikalık ortalama: en fazla 1440 kayıt
/// Zamanlar epoch'tan saniye/dakika (int) olarak tutulur: kayıt başına 8 bayt, dolu bir sensör ~40 KB.
/// Tamponlar veri geldikçe büyür; hiç değer okunmayan sensörler bellek harcamaz.
/// </summary>
public sealed class SensorHistory
{
    private const int SecondCapacity = 3600;
    private const int MinuteCapacity = 1440;

    private static readonly DateTime Epoch = DateTime.Now;
    private static readonly DateTime EpochMinute = Epoch.AddTicks(-(Epoch.Ticks % TimeSpan.TicksPerMinute));

    private readonly Ring _seconds = new(SecondCapacity);
    private readonly Ring _minutes = new(MinuteCapacity);

    private int _curMinute = int.MinValue;
    private double _sum;
    private int _n;

    public int Count => _seconds.Count;

    /// <summary>En eskiden en yeniye i'inci saniyelik değer.</summary>
    public float this[int i] => _seconds.ValueAt(i);

    /// <summary>Yeni ölçüm ekler; bir dakika tamamlandıysa o dakikanın özetini döndürür.</summary>
    public MinuteValue? Add(DateTime time, float value)
    {
        _seconds.Push((int)(time - Epoch).TotalSeconds, value);

        var minute = MinuteIndex(time);
        MinuteValue? completed = null;
        if (_n > 0 && minute != _curMinute)
        {
            var avg = (float)(_sum / _n);
            _minutes.Push(_curMinute, avg);
            completed = new MinuteValue(MinuteTime(_curMinute), avg);
            _sum = 0;
            _n = 0;
        }
        _curMinute = minute;
        _sum += value;
        _n++;
        return completed;
    }

    /// <summary>Diskten okunan eski dakikaları başa ekler (uygulama yeniden açıldığında 24 saat dolu gelsin).</summary>
    public void Prefill(IReadOnlyList<MinuteValue> older)
    {
        if (older.Count == 0) return;
        var firstOwn = _minutes.Count > 0 ? _minutes.TimeAt(0) : int.MaxValue;
        var merged = new List<(int Time, float Value)>(Math.Min(older.Count + _minutes.Count, MinuteCapacity));
        foreach (var m in older)
        {
            var t = MinuteIndex(m.Minute);
            if (t < firstOwn) merged.Add((t, m.Avg));
        }
        for (var i = 0; i < _minutes.Count; i++) merged.Add((_minutes.TimeAt(i), _minutes.ValueAt(i)));
        _minutes.Reset(merged.Count > MinuteCapacity ? merged.GetRange(merged.Count - MinuteCapacity, MinuteCapacity) : merged);
    }

    /// <summary>Grafik için noktalar. 1 saate kadar saniyelik, üstü dakikalık veri.</summary>
    public List<TimedValue> GetPoints(TimeSpan range, DateTime now)
    {
        var from = now - range;
        if (range <= TimeSpan.FromHours(1))
        {
            var fromSec = (int)(from - Epoch).TotalSeconds;
            var first = _seconds.FirstIndexAtOrAfter(fromSec);
            var result = new List<TimedValue>(_seconds.Count - first);
            for (var i = first; i < _seconds.Count; i++)
                result.Add(new TimedValue(Epoch.AddSeconds(_seconds.TimeAt(i)), _seconds.ValueAt(i)));
            return result;
        }
        else
        {
            var first = _minutes.FirstIndexAtOrAfter(MinuteIndex(from) + (from.Second > 0 || from.Millisecond > 0 ? 1 : 0));
            var result = new List<TimedValue>(_minutes.Count - first + 1);
            for (var i = first; i < _minutes.Count; i++)
                result.Add(new TimedValue(MinuteTime(_minutes.TimeAt(i)).AddSeconds(30), _minutes.ValueAt(i)));
            if (_n > 0) result.Add(new TimedValue(now, (float)(_sum / _n)));
            return result;
        }
    }

    public IEnumerable<MinuteValue> Minutes(DateTime from)
    {
        var start = MinuteIndex(from) + (from.Second > 0 || from.Millisecond > 0 ? 1 : 0);
        for (var i = _minutes.FirstIndexAtOrAfter(start); i < _minutes.Count; i++)
            yield return new MinuteValue(MinuteTime(_minutes.TimeAt(i)), _minutes.ValueAt(i));
    }

    private static int MinuteIndex(DateTime t) => (int)Math.Floor((t - EpochMinute).TotalMinutes);
    private static DateTime MinuteTime(int index) => EpochMinute.AddMinutes(index);

    /// <summary>
    /// (zaman, değer) halka tamponu. Dolana kadar doğrusal büyür (başlangıç hep 0), dolunca en eskinin üstüne yazar.
    /// Zamanlar artan sırada eklendiği için aralık başı ikili aramayla bulunur.
    /// </summary>
    private sealed class Ring(int capacity)
    {
        private int[] _times = [];
        private float[] _values = [];
        private int _start;

        public int Count { get; private set; }

        public int TimeAt(int i) => _times[(_start + i) % _times.Length];
        public float ValueAt(int i) => _values[(_start + i) % _values.Length];

        public void Push(int time, float value)
        {
            if (Count < capacity)
            {
                if (Count == _times.Length) Grow();
                _times[Count] = time;
                _values[Count] = value;
                Count++;
                return;
            }
            _times[_start] = time;
            _values[_start] = value;
            _start = (_start + 1) % capacity;
        }

        public void Reset(List<(int Time, float Value)> items)
        {
            var size = Math.Max(items.Count, Math.Min(_times.Length, capacity));
            if (_times.Length < size)
            {
                _times = new int[size];
                _values = new float[size];
            }
            for (var i = 0; i < items.Count; i++) (_times[i], _values[i]) = items[i];
            _start = 0;
            Count = items.Count;
        }

        /// <summary>Zamanı <paramref name="time"/> ya da sonrası olan ilk kaydın sırası (yoksa Count).</summary>
        public int FirstIndexAtOrAfter(int time)
        {
            int lo = 0, hi = Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (TimeAt(mid) < time) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        private void Grow()
        {
            // Dolmadan önce başlangıç hep 0: doğrudan kopyalanabilir.
            var size = Math.Min(capacity, Math.Max(64, _times.Length * 2));
            Array.Resize(ref _times, size);
            Array.Resize(ref _values, size);
        }
    }
}

/// <summary>
/// Dakikalık ortalamaları günlük CSV dosyalarına yazar: %AppData%\Kor\history\yyyy-MM-dd.csv
/// Biçim: "#cols,id1,id2,..." başlığı + "zaman,değer1,değer2,..." satırları (sabit kültür).
/// Disk işlemi arka planda tek bir kuyrukla yapılır.
/// </summary>
public sealed class HistoryRecorder : IDisposable
{
    public const int KeepDays = 7;

    private readonly string _folder;
    private readonly Channel<(DateOnly Day, string Text)> _queue = Channel.CreateUnbounded<(DateOnly, string)>(new() { SingleReader = true });
    private readonly Task _writer;
    private string[]? _cols;
    private DateOnly _day;

    public HistoryRecorder(string folder)
    {
        _folder = folder;
        _writer = Task.Run(WriteLoop);
    }

    public static string FileFor(string folder, DateOnly day) => Path.Combine(folder, $"{day:yyyy-MM-dd}.csv");

    public void Append(DateTime minute, IReadOnlyList<(string Id, float Avg)> values)
    {
        if (values.Count == 0) return;
        var inv = CultureInfo.InvariantCulture;
        var day = DateOnly.FromDateTime(minute);
        var ids = values.Select(v => v.Id).ToArray();
        var sb = new StringBuilder();

        if (_cols is null || day != _day || !ids.AsSpan().SequenceEqual(_cols))
        {
            sb.Append("#cols");
            foreach (var id in ids) sb.Append(',').Append(id.Replace(",", "%2C"));
            sb.Append('\n');
            _cols = ids;
            _day = day;
        }

        sb.Append(minute.ToString("yyyy-MM-ddTHH:mm", inv));
        foreach (var (_, avg) in values) sb.Append(',').Append(float.IsFinite(avg) ? avg.ToString("0.###", inv) : "");
        sb.Append('\n');
        _queue.Writer.TryWrite((day, sb.ToString()));
    }

    private async Task WriteLoop()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            Cleanup();
            await foreach (var (day, text) in _queue.Reader.ReadAllAsync())
            {
                try { await File.AppendAllTextAsync(FileFor(_folder, day), text); }
                catch (Exception ex) { Log.Write("Geçmiş yazılamadı", ex); }
            }
        }
        catch (Exception ex)
        {
            Log.Write("Geçmiş kaydedici durdu", ex);
        }
    }

    private void Cleanup()
    {
        var limit = DateOnly.FromDateTime(DateTime.Now).AddDays(-KeepDays);
        foreach (var file in Directory.EnumerateFiles(_folder, "*.csv"))
        {
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", out var d) && d < limit)
            {
                try { File.Delete(file); } catch { /* yoksay */ }
            }
        }
    }

    /// <summary>Son 24 saatin dakikalık verisini okur (uygulama açılışında arka planda).</summary>
    public static Dictionary<string, List<MinuteValue>> Load(string folder, DateTime since)
    {
        var result = new Dictionary<string, List<MinuteValue>>();
        if (!Directory.Exists(folder)) return result;
        var inv = CultureInfo.InvariantCulture;

        for (var day = DateOnly.FromDateTime(since); day <= DateOnly.FromDateTime(DateTime.Now); day = day.AddDays(1))
        {
            var path = FileFor(folder, day);
            if (!File.Exists(path)) continue;
            string[] cols = [];
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("#cols", StringComparison.Ordinal))
                {
                    cols = line.Split(',').Skip(1).Select(c => c.Replace("%2C", ",")).ToArray();
                    continue;
                }
                var parts = line.Split(',');
                if (parts.Length < 2 || !DateTime.TryParseExact(parts[0], "yyyy-MM-ddTHH:mm", inv, DateTimeStyles.None, out var t) || t < since) continue;
                for (var i = 1; i < parts.Length && i - 1 < cols.Length; i++)
                {
                    if (!float.TryParse(parts[i], NumberStyles.Float, inv, out var v)) continue;
                    if (!result.TryGetValue(cols[i - 1], out var list)) result[cols[i - 1]] = list = [];
                    list.Add(new MinuteValue(t, v));
                }
            }
        }
        return result;
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(2)); } catch { /* yoksay */ }
    }
}

/// <summary>Excel'de doğru açılsın diye bölgesel ayraç (Türkçe'de ';') ve ondalık virgülüyle CSV.</summary>
public static class CsvExport
{
    public static void Write(string path, IReadOnlyList<string> headers, IEnumerable<(DateTime Time, IReadOnlyList<float?> Values)> rows)
    {
        var culture = CultureInfo.CurrentCulture;
        var sep = culture.TextInfo.ListSeparator;
        var sb = new StringBuilder();
        sb.AppendJoin(sep, headers.Prepend("Zaman").Select(Quote)).Append("\r\n");
        foreach (var (time, values) in rows)
        {
            sb.Append(time.ToString("yyyy-MM-dd HH:mm:ss", culture));
            foreach (var v in values) sb.Append(sep).Append(v is float f ? f.ToString("0.##", culture) : "");
            sb.Append("\r\n");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        string Quote(string s) => s.Contains(sep) || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
