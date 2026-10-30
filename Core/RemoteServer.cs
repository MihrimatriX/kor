using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using QRCoder;

namespace Kor.Core;

/// <summary>
/// Telefondan izleme için küçük, bağımlılıksız HTTP sunucusu (TcpListener).
/// HttpListener yerine TcpListener: yönetici izni / URL ACL gerektirmez.
/// Güvenlik: sadece özel IP aralıklarından gelen istekler; her istek rastgele bir bağlantı kodu (token) ister.
/// Sadece okuma: hiçbir uç nokta ayar değiştirmez ya da fanlara dokunmaz.
/// Uç noktalar: "/" → mobil sayfa, "/api/data" → anlık JSON.
/// </summary>
public sealed class RemoteServer : IDisposable
{
    private readonly Func<string> _json;
    private readonly string _token;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private static readonly Lazy<byte[]> Page = new(LoadPage);

    public int Port { get; }

    public RemoteServer(int port, string token, Func<string> json)
    {
        Port = port;
        _token = token;
        _json = json;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _ = AcceptLoop();
    }

    public static string? LanAddress()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .Select(n => (Props: n.GetIPProperties(), Nic: n))
                .Where(x => x.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                .SelectMany(x => x.Props.UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                .Select(a => a.Address.ToString())
                .ToList();
            return candidates.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public static BitmapImage? Qr(string text, bool light)
    {
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            byte[] dark = light ? [0x12, 0x18, 0x26, 0xFF] : [0xEE, 0xF2, 0xF7, 0xFF];
            byte[] back = light ? [0xFF, 0xFF, 0xFF, 0xFF] : [0x14, 0x19, 0x22, 0xFF];
            var png = new PngByteQRCode(data).GetGraphic(8, dark, back);
            var image = new BitmapImage();
            using var ms = new MemoryStream(png);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Write("QR kod üretilemedi", ex);
            return null;
        }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = Handle(client);
        }
    }

    /// <summary>Sadece yerel ağdan (özel IP aralıkları) gelen bağlantılar kabul edilir.</summary>
    private static bool IsLocalNetwork(EndPoint? endPoint)
    {
        if (endPoint is not IPEndPoint ip) return false;
        var address = ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        var b = address.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254)
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // CGNAT / Tailscale
    }

    private bool TokenMatches(string? token)
        => token is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(_token));

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            if (!IsLocalNetwork(client.Client.RemoteEndPoint)) return;
            try
            {
                client.ReceiveTimeout = 5000;
                var stream = client.GetStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(5000);

                // İstek satırı + başlıklar (en fazla 8 KB)
                var buffer = new byte[8192];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
                    if (n == 0) break;
                    read += n;
                    if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n")) break;
                }
                var request = Encoding.ASCII.GetString(buffer, 0, read);
                var firstLine = request.Split("\r\n")[0].Split(' ');
                if (firstLine.Length < 2 || firstLine[0] != "GET")
                {
                    await Respond(stream, 405, "text/plain", "Method Not Allowed"u8.ToArray(), timeout.Token);
                    return;
                }

                var target = firstLine[1];
                var path = target.Split('?')[0];
                var query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";
                var token = query.Split('&').Select(p => p.Split('=')).FirstOrDefault(p => p[0] == "t" && p.Length == 2)?[1];

                if (path is "/favicon.ico")
                {
                    await Respond(stream, 404, "text/plain", [], timeout.Token);
                    return;
                }
                if (!TokenMatches(token))
                {
                    await Respond(stream, 401, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Kor: bağlantı kodu geçersiz. QR kodu yeniden okutun."), timeout.Token);
                    return;
                }

                switch (path)
                {
                    case "/":
                        await Respond(stream, 200, "text/html; charset=utf-8", Page.Value, timeout.Token);
                        break;
                    case "/api/data":
                        await Respond(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_json()), timeout.Token);
                        break;
                    default:
                        await Respond(stream, 404, "text/plain", "Not Found"u8.ToArray(), timeout.Token);
                        break;
                }
            }
            catch
            {
                // Bağlantı koptu / zaman aşımı: yoksay
            }
        }
    }

    private static async Task Respond(NetworkStream stream, int code, string type, byte[] body, CancellationToken ct)
    {
        var status = code switch { 200 => "OK", 401 => "Unauthorized", 404 => "Not Found", _ => "Error" };
        var header = $"HTTP/1.1 {code} {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                     "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await stream.WriteAsync(body, ct);
    }

    private static byte[] LoadPage()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Kor.remote.html");
        if (s is null) return "<h1>Kor</h1>"u8.ToArray();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* yoksay */ }
    }
}
