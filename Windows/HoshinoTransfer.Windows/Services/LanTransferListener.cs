using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;

namespace HoshinoTransfer.Windows.Services;

/// <summary>
/// Minimal HTTP chunk server over a raw TCP listener so no admin URLACL is needed.
/// Serves transfer chunks to the accepted receiver over the local network.
/// </summary>
public sealed class LanTransferListener : IDisposable
{
    public const int DefaultPort = 52317;
    private readonly TcpListener _listener;
    private readonly string _token;
    private readonly ConcurrentDictionary<string, TransferSource> _items = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _servedBytes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public string Token => _token;
    public int Port { get; private set; }
    public bool IsRunning { get; private set; }

    public LanTransferListener(string? token = null)
    {
        _token = token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _listener = new TcpListener(IPAddress.Any, 0);
    }

    public static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>Best-effort LAN IPv4: the local address the OS would use to reach the internet.</summary>
    public static string? GetLocalIPv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch { return null; }
    }

    public void Publish(string itemId, TransferSource source) => _items[itemId] = source;

    public void Start()
    {
        if (IsRunning) return;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        IsRunning = true;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { if (ct.IsCancellationRequested) return; continue; }
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private static async Task<string?> ReadRequestHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var head = new MemoryStream();
        while (head.Length < 8192)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0) return null;
            head.Write(buffer, 0, read);
            var bytes = head.ToArray();
            var terminator = Encoding.ASCII.GetString(bytes).IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (terminator >= 0) return Encoding.ASCII.GetString(bytes, 0, terminator);
        }
        return null;
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 10_000;
                client.SendTimeout = 30_000;
                await using var stream = client.GetStream();
                var head = await ReadRequestHeadAsync(stream, ct);
                if (head is null) return;
                var requestLine = head.Split("\r\n")[0];
                var parts = requestLine.Split(' ');
                if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase)) { await WriteAsync(stream, "405 Method Not Allowed", [], ct); return; }

                var queryIndex = parts[1].IndexOf('?');
                var path = queryIndex < 0 ? parts[1] : parts[1][..queryIndex];
                var query = queryIndex < 0 ? "" : parts[1][(queryIndex + 1)..];
                var presentedToken = query
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(pair => pair.Split('=', 2))
                    .Where(pair => pair.Length == 2 && pair[0].Equals("token", StringComparison.OrdinalIgnoreCase))
                    .Select(pair => Uri.UnescapeDataString(pair[1]))
                    .FirstOrDefault();
                if (presentedToken is null || !CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(presentedToken), Encoding.ASCII.GetBytes(_token)))
                {
                    await WriteAsync(stream, "403 Forbidden", Encoding.ASCII.GetBytes("invalid capability token"), ct);
                    return;
                }

                var segments = path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length != 4 || !segments[0].Equals("hoshino", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(stream, "404 Not Found", Encoding.ASCII.GetBytes("unknown route"), ct);
                    return;
                }
                var itemId = segments[2];
                if (!int.TryParse(segments[3], out var chunkIndex) || chunkIndex < 0 || !_items.TryGetValue(itemId, out var source))
                {
                    await WriteAsync(stream, "404 Not Found", Encoding.ASCII.GetBytes("unknown transfer item"), ct);
                    return;
                }

                var chunkSize = 4 * 1024 * 1024;
                var offset = (long)chunkIndex * chunkSize;
                if (offset >= source.Size) { await WriteAsync(stream, "404 Not Found", Encoding.ASCII.GetBytes("chunk out of range"), ct); return; }
                var length = (int)Math.Min(chunkSize, source.Size - offset);

                await using var file = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                file.Position = offset;
                await WriteAsync(stream, "200 OK", null, ct, contentLength: length);
                var buffer = new byte[128 * 1024];
                long remaining = length;
                while (remaining > 0)
                {
                    var wanted = (int)Math.Min(buffer.Length, remaining);
                    var read = await file.ReadAsync(buffer.AsMemory(0, wanted), ct);
                    if (read == 0) throw new EndOfStreamException("source file ended early");
                    await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                    remaining -= read;
                    _servedBytes.AddOrUpdate(itemId, read, (_, total) => total + read);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* socket-level faults: the receiver retries or falls back */ }
        }
    }

    private static async Task WriteAsync(NetworkStream stream, string status, byte[]? body, CancellationToken ct, int contentLength = -1)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        builder.Append("Connection: close\r\n");
        builder.Append("Cache-Control: no-store\r\n");
        builder.Append("Content-Type: application/octet-stream\r\n");
        builder.Append("Content-Length: ").Append(contentLength >= 0 ? contentLength : (body?.Length ?? 0)).Append("\r\n");
        builder.Append("\r\n");
        var header = Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(header.AsMemory(0, header.Length), ct);
        if (body is { Length: > 0 }) await stream.WriteAsync(body.AsMemory(0, body.Length), ct);
        await stream.FlushAsync(ct);
    }

    public long ServedBytesFor(string itemId) => _servedBytes.TryGetValue(itemId, out var total) ? total : 0;

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
