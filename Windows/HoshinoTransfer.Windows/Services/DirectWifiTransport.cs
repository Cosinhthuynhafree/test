using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using HoshinoTransfer.Windows.Models;

namespace HoshinoTransfer.Windows.Services;

/// <summary>
/// Direct Wi-Fi transport: the sender serves chunks over the local network and the
/// receiver fetches them directly. The server brokers control messages only.
/// Falls back to the Server Relay when the receiver cannot reach the LAN endpoint.
/// </summary>
public sealed class DirectWifiTransport : ITransferTransport
{
    public const string TransportName = "Direct Wi-Fi";
    private readonly ApiClient _api;
    private readonly ServerRelayTransport _relay;

    public DirectWifiTransport(ApiClient api, ServerRelayTransport relay)
    {
        _api = api;
        _relay = relay;
    }

    public TransferMode Mode => TransferMode.DirectWifi;
    public bool IsAvailable => LanTransferListener.GetLocalIPv4() is not null;
    public string AvailabilityReason => IsAvailable
        ? "Both devices on the same Wi-Fi/LAN can exchange bytes directly."
        : "No LAN IPv4 address is available on this machine.";

    public Task UploadItemAsync(string transferId, TransferSource source, int chunkSize, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        // The sender-side direct flow is orchestrated per-transfer (all items at once)
        // because it owns one shared listener; UploadAcceptedAsync drives it.
        return Task.FromException(new NotSupportedException("Use UploadAcceptedAsync for the Direct Wi-Fi sender flow."));
    }

    public async Task<TransferDto> UploadAcceptedAsync(TransferDto transfer, IReadOnlyList<TransferSource> sources,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        using var listener = new LanTransferListener();
        foreach (var source in sources) listener.Publish(source.ItemId, source);
        listener.Start();
        var lanIp = LanTransferListener.GetLocalIPv4()
                    ?? throw new InvalidOperationException("No LAN IPv4 address is available for Direct Wi-Fi.");
        await _api.RegisterDirectEndpointAsync(transfer.Id, lanIp, listener.Port, listener.Token, cancellationToken);

        TransferDto? terminal = null;
        var started = DateTimeOffset.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await _api.GetTransferAsync(transfer.Id, cancellationToken);
            if (state.Status is "Completed" or "Failed" or "Cancelled") { terminal = state; break; }
            if (state.RelayRequested || DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(15)) break;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        listener.Stop();
        if (terminal is { Status: "Completed" }) return terminal;

        // Receiver could not reach the LAN endpoint (or asked explicitly): finish over the relay.
        foreach (var source in sources)
            await _relay.UploadItemAsync(transfer.Id, source, transfer.ChunkSize, progress, cancellationToken);
        return await _api.CompleteTransferAsync(transfer.Id, cancellationToken);
    }

    public async Task DownloadItemAsync(string transferId, TransferItemDto item, string destinationPath, CancellationToken cancellationToken)
    {
        var transfer = await _api.GetTransferAsync(transferId, cancellationToken);
        var direct = transfer.DirectInfo;
        if (direct is null || string.IsNullOrWhiteSpace(direct.Token))
            throw new NotSupportedException("No direct channel is registered for this transfer.");
        await DownloadItemDirectAsync(transferId, item, direct, destinationPath, cancellationToken);
    }

    /// <summary>Receiver-side orchestration: direct first, verified, then relay fallback.</summary>
    public async Task<IReadOnlyList<string>> ReceiveAllAsync(TransferDto transfer, string destinationDirectory,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var direct = transfer.DirectInfo;
        var saved = new List<string>(transfer.Items.Count);
        if (direct is not null && await TryReachAsync(direct, cancellationToken))
        {
            var allVerified = true;
            foreach (var item in transfer.Items)
            {
                var name = Path.GetFileName(item.FileName);
                if (string.IsNullOrWhiteSpace(name)) name = "received-file";
                var destination = Path.Combine(destinationDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{name}");
                try
                {
                    await DownloadItemDirectAsync(transfer.Id, item, direct, destination, cancellationToken);
                    saved.Add(destination);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    allVerified = false;
                    break;
                }
            }
            if (allVerified)
            {
                var completed = await _api.CompleteDirectTransferAsync(transfer.Id, cancellationToken);
                progress?.Report(new TransferProgress(transfer.Id, "all files", 1, 1, 0, TimeSpan.Zero, Mode));
                return saved;
            }
            foreach (var path in saved) { try { File.Delete(path); } catch { } }
            saved.Clear();
        }

        await _api.RequestRelayFallbackAsync(transfer.Id, cancellationToken);
        var relayFinal = await WaitForCompletionAsync(transfer.Id, cancellationToken);
        var relayTransport = new ServerRelayTransport(_api);
        foreach (var item in relayFinal.Items)
        {
            var name = Path.GetFileName(item.FileName);
            if (string.IsNullOrWhiteSpace(name)) name = "received-file";
            var destination = Path.Combine(destinationDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{name}");
            await relayTransport.DownloadItemAsync(transfer.Id, item, destination, cancellationToken);
            saved.Add(destination);
        }
        return saved;
    }

    private static async Task<bool> TryReachAsync(DirectEndpointInfo endpoint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Port is not int port) return false;
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(endpoint.Host, port, timeout.Token);
            return tcp.Connected;
        }
        catch { return false; }
    }

    private async Task DownloadItemDirectAsync(string transferId, TransferItemDto item, DirectEndpointInfo direct,
        string destinationPath, CancellationToken cancellationToken)
    {
        var chunkSize = 4 * 1024 * 1024;
        var chunkCount = (int)Math.Ceiling(item.Size / (double)chunkSize);
        await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        for (var index = 0; index < chunkCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = $"http://{direct.Host}:{direct.Port}/hoshino/{transferId}/{item.Id}/{index}?token={Uri.EscapeDataString(direct.Token ?? "")}";
            await using var chunk = await _api.DownloadDirectChunkAsync(url, cancellationToken);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await chunk.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
            }
        }
        if (received != item.Size) throw new IOException($"Direct download of {item.FileName} ended early.");
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(item.Sha256)))
        {
            File.Delete(destinationPath);
            throw new IOException($"Direct download of {item.FileName} failed SHA-256 verification.");
        }
    }

    private async Task<TransferDto> WaitForCompletionAsync(string transferId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddHours(24);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await _api.GetTransferAsync(transferId, cancellationToken);
            if (current.Status is "Completed" or "Failed" or "Cancelled") return current;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException("Relay fallback transfer did not finish in time.");
    }
}
