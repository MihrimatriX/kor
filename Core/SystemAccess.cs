using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Kor.Core;

/// <summary>Yönetici izni ve PawnIO sürücüsü ile ilgili yardımcılar.</summary>
public static class Elevation
{
    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Uygulamayı UAC ile yeniden başlatır. Kullanıcı iptal ederse false döner.</summary>
    public static bool TryRelaunchElevated(IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', args.Where(a => a != "--elevated").Append("--elevated").Select(Quote)),
                WorkingDirectory = AppContext.BaseDirectory,
            };
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Write("Yönetici olarak yeniden başlatılamadı", ex);
            return false;
        }
    }

    /// <summary>
    /// LibreHardwareMonitor 0.9.5+ CPU (MSR) ve anakart (Super I/O) sensörleri için
    /// imzalı PawnIO sürücüsünü kullanıyor. Kurulumu bu kayıt anahtarından anlaşılıyor.
    /// </summary>
    public static bool IsPawnIoInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void InstallPawnIoWithWinget()
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/k echo PawnIO kuruluyor (winget)... & winget install --id namazso.PawnIO -e & echo. & echo Kurulum bittiyse Kor'u yeniden baslatin.")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Write("winget başlatılamadı", ex);
            OpenUrl("https://pawnio.eu");
        }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Bağlantı açılamadı: {url}", ex); }
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}

/// <summary>
/// "Windows ile başlat": yönetici yetkisiyle (UAC sormadan) açılması için
/// Görev Zamanlayıcı'da oturum açılışında çalışan bir görev oluşturur.
/// (Run kayıt anahtarı yönetici yetkisi veremez; her açılışta UAC sorardı.)
/// </summary>
public static class StartupTask
{
    private const string TaskName = "Kor Hardware Monitor";

    public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

    public static bool Enable(out string? error)
    {
        error = null;
        var exe = Environment.ProcessPath!;
        var user = WindowsIdentity.GetCurrent().Name;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>Kor</Author>
                <Description>Kor donanim izleyicisini oturum acilisinda baslatir.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>--tray</Arguments>
                  <WorkingDirectory>{SecurityElement.Escape(AppContext.BaseDirectory)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        var tmp = Path.Combine(Path.GetTempPath(), $"kor-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            var code = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F", out var output);
            if (code != 0) error = string.IsNullOrWhiteSpace(output) ? $"schtasks hata kodu {code}" : output.Trim();
            return code == 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* yoksay */ }
        }
    }

    public static bool Disable(out string? error)
    {
        var code = RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out var output);
        error = code == 0 ? null : output.Trim();
        return code == 0;
    }

    private static int RunSchtasks(string args, out string output)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            p.WaitForExit(10_000);
            output = (stderr.Result + stdout.Result).Trim();
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }
}

/// <summary>Tek örnek kilidi + ikinci açılışta mevcut örneğe "pencereni göster" sinyali.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string Name = @"Local\Kor.HardwareMonitor";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly EventWaitHandle _exitEvent;
    private readonly RegisteredWaitHandle _wait;
    private readonly RegisteredWaitHandle _exitWait;

    public event Action? ShowRequested;
    /// <summary>Kurulum/kaldırma "Kor.exe --exit" ile düzgün kapanma isteyince (fanlar BIOS'a döner).</summary>
    public event Action? ExitRequested;

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent, EventWaitHandle exitEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        _exitEvent = exitEvent;
        _wait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => ShowRequested?.Invoke(), null, -1, false);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => ExitRequested?.Invoke(), null, -1, true);
    }

    public static SingleInstance? TryAcquire()
    {
        try
        {
            var mutex = new Mutex(true, Name, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return null;
            }
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, Name + ".Show");
            var exit = new EventWaitHandle(false, EventResetMode.AutoReset, Name + ".Exit");
            return new SingleInstance(mutex, ev, exit);
        }
        catch (UnauthorizedAccessException)
        {
            // Diğer örnek yönetici olarak çalışıyor ve kilide erişemiyoruz → zaten açık.
            return null;
        }
    }

    public static void SignalExisting()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(Name + ".Show", out var ev))
            {
                using (ev) ev.Set();
            }
        }
        catch
        {
            // Yetki farkı varsa sinyal gönderilemez; sessizce çık.
        }
    }

    /// <summary>Çalışan örneğe "kapan" der ve kapanana kadar (en fazla <paramref name="timeout"/>) bekler.</summary>
    public static bool SignalExitAndWait(TimeSpan timeout)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(Name + ".Exit", out var ev)) return true; // çalışmıyor
            using (ev) ev.Set();

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!Mutex.TryOpenExisting(Name, out var m)) return WaitForOtherInstancesToExit(deadline);
                m.Dispose();
                Thread.Sleep(200);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Kilit, uygulama kapanmaya başlarken bırakılır; süreç birkaç yüz ms daha yaşayıp dosyalarını tutabilir.
    /// Kurulum dosyaları değiştirmeden önce aynı exe'den çalışan diğer Kor süreçlerinin gerçekten bitmesini bekler.
    /// </summary>
    private static bool WaitForOtherInstancesToExit(DateTime deadline)
    {
        var self = Environment.ProcessId;
        var path = Environment.ProcessPath;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(path ?? "Kor")))
        {
            using (p)
            {
                try
                {
                    if (p.Id == self) continue;
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || !p.WaitForExit(left)) return false;
                }
                catch
                {
                    // Erişilemeyen ya da bu arada kapanan süreç
                }
            }
        }
        return true;
    }

    public void Dispose()
    {
        _wait.Unregister(null);
        _exitWait.Unregister(null);
        _exitEvent.Dispose();
        _showEvent.Dispose();
        try { _mutex.ReleaseMutex(); } catch { /* başka thread'deyse */ }
        _mutex.Dispose();
    }
}
