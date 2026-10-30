using System.IO;

namespace Kor.Core;

/// <summary>Basit, thread-safe dosya günlüğü: %AppData%\Kor\kor.log (1 MB'ı geçince sıfırlanır).</summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static string FilePath => Path.Combine(SettingsStore.Folder, "kor.log");

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000) info.Delete();
                File.AppendAllText(FilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{(ex is null ? "" : Environment.NewLine + ex)}{Environment.NewLine}");
            }
        }
        catch
        {
            // Günlük yazılamazsa uygulamayı düşürme.
        }
    }
}
