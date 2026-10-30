using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace Kor.Core;

public enum ChartRange { FiveMinutes, Hour, Day }

/// <summary>Zaman damgalı tek değer.</summary>
public readonly record struct TimedValue(DateTime Time, float Value);

/// <summary>Dakikalık özet (24 saatlik grafik ve diske kayıt için).</summary>
public readonly record struct MinuteValue(DateTime Minute, float Avg, float Max);

/// <summary>
/// Bir sensörün geçmişi:
///  • Son 1 saat, her ölçüm (saniye çözünürlüğü) — 3600 kayıt
///  • Son 24 saat, dakikalık ortalama/maksimum — 1440 kayıt
/// Bellekte sabit boyutlu halka tamponlar; sensör başına ~50 KB.
/// </summary>
public sealed class SensorHistory
{
    private static readonly DateTime Epoch = DateTime.Now;

    // Saniyelik: zamanı epoch'tan saniye (int) olarak tutarak kayıt başına 8 bayt.
    private readonly int[] _secTimes = new int[3600];
    private readonly float[] _secValues = new float[3600];
    private int _secStart, _secCount;

    private readonly MinuteValue[] _minutes = new MinuteValue[1440];
    private int _minStart, _minCount;

    private DateTime _curMinute;
    private double _sum;
    private int _n;
    private float _max = float.MinValue;

    public int Count => _secCount;

    /// <summary>En eskiden en yeniye i'inci saniyelik değer.</summary>
    public float this[int i] => _secValues[(_secStart + i) % _secValues.Length];

    /// <summary>Yeni ölçüm ekler; bir dakika tamamlandıysa o dakikanın özetini döndürür.</summary>
    public MinuteValue? Add(DateTime time, float value)
    {
        var sec = (int)(time - Epoch).TotalSeconds;
        var idx = (_secStart + _secCount) % _secValues.Length;
        if (_secCount < _secValues.Length) _secCount++;
        else _secStart = (_secStart + 1) % _secValues.Length;
        _secTimes[idx] = sec;
        _secValues[idx] = value;

        var minute = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, time.Kind);
        MinuteValue? completed = null;
        if (_n > 0 && minute != _curMinute)
        {
            completed = new MinuteValue(_curMinute, (float)(_sum / _n), _max);
            PushMinute(completed.Value);
            _sum = 0;
            _n = 0;
            _max = float.MinValue;
        }
        _curMinute = minute;
        _sum += value;
        _n++;
        _max = Math.Max(_max, value);
        return completed;
    }

    /// <summary>Diskten okunan eski dakikaları başa ekler (uygulama yeniden açıldığında 24 saat dolu gelsin).</summary>
    public void Prefill(IReadOnlyList<MinuteValue> older)
    {
        if (older.Count == 0) return;
        var firstOwn = _minCount > 0 ? _minutes[_minStart].Minute : DateTime.MaxValue;
        var existing = Enumerable.Range(0, _minCount).Select(i => _minutes[(_minStart + i) % _minutes.Length]).ToList();
        var merged = older.Where(m => m.Minute < firstOwn).Concat(existing).TakeLast(_minutes.Length).ToList();
        _minStart = 0;
        _minCount = merged.Count;
        for (var i = 0; i < merged.Count; i++) _minutes[i] = merged[i];
    }

    private void PushMinute(MinuteValue m)
    {
        var idx = (_minStart + _minCount) % _minutes.Length;
        if (_minCount < _minutes.Length) _minCount++;
        else _minStart = (_minStart + 1) % _minutes.Length;
        _minutes[idx] = m;
    }

    /// <summary>Grafik için noktalar. 1 saate kadar saniyelik, üstü dakikalık veri.</summary>
    public List<TimedValue> GetPoints(TimeSpan range, DateTime now)
    {
        var from = now - range;
        var result = new List<TimedValue>();
        if (range <= TimeSpan.FromHours(1))
        {
            var fromSec = (int)(from - Epoch).TotalSeconds;
            for (var i = 0; i < _secCount; i++)
            {
                var k = (_secStart + i) % _secValues.Length;
                if (_secTimes[k] >= fromSec) result.Add(new TimedValue(Epoch.AddSeconds(_secTimes[k]), _secValues[k]));
            }
        }
        else
        {
            for (var i = 0; i < _minCount; i++)
            {
                var m = _minutes[(_minStart + i) % _minutes.Length];
                if (m.Minute >= from) result.Add(new TimedValue(m.Minute.AddSeconds(30), m.Avg));
            }
            if (_n > 0) result.Add(new TimedValue(now, (float)(_sum / _n)));
        }
        return result;
    }

    public IEnumerable<MinuteValue> Minutes(DateTime from)
    {
        for (var i = 0; i < _minCount; i++)
        {
            var m = _minutes[(_minStart + i) % _minutes.Length];
            if (m.Minute >= from) yield return m;
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
                    list.Add(new MinuteValue(t, v, v));
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
