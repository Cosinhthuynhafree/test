using System.IO;
using System.Net.Http;
using HoshinoTransfer.Windows.Models;

namespace HoshinoTransfer.Windows.Services;

public enum TransferStatus { Pending, Connecting, Transferring, Paused, Completed, Cancelled, Failed }
public enum TransferMode { P2P, DirectWifi, ServerRelay, Lightning }

public sealed record TransferProgress(string TransferId, string FileName, long TotalBytes, long TransferredBytes, double BytesPerSecond, TimeSpan? Remaining, TransferMode Mode);
public sealed record TransferSource(string ItemId, string FileName, string Path, long Size);

public interface ITransferTransport
{
    TransferMode Mode { get; }
    bool IsAvailable { get; }
    string AvailabilityReason { get; }
    Task UploadItemAsync(string transferId, TransferSource source, int chunkSize, IProgress<TransferProgress>? progress, CancellationToken cancellationToken);
    Task DownloadItemAsync(string transferId, TransferItemDto item, string destinationPath, CancellationToken cancellationToken);
}

public sealed class UnavailableTransferTransport(TransferMode mode, string reason) : ITransferTransport
{
    public TransferMode Mode { get; } = mode;
    public bool IsAvailable => false;
    public string AvailabilityReason { get; } = reason;
    public Task UploadItemAsync(string transferId, TransferSource source, int chunkSize, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException($"{Mode} is unavailable: {AvailabilityReason}"));
    public Task DownloadItemAsync(string transferId, TransferItemDto item, string destinationPath, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException($"{Mode} is unavailable: {AvailabilityReason}"));
}

public sealed class ServerRelayTransport : ITransferTransport
{
    private readonly ApiClient _api;
    /// <summary>1.0 = unthrottled; 0.7 = capped at 70 percent of the natural speed.</summary>
    public double SpeedFactor { get; set; } = 1.0;
    public TransferMode Mode => TransferMode.ServerRelay;
    public bool IsAvailable => true;
    public string AvailabilityReason => "Authenticated HTTPS chunk relay is available.";
    public ServerRelayTransport(ApiClient api) { _api = api; }

    public async Task UploadItemAsync(string transferId, TransferSource source, int chunkSize, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        if (chunkSize is <= 0 or > 4 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        var backoff = 1;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long newlySent = 0;
        var buffer = new byte[chunkSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await _api.GetTransferProgressAsync(transferId, cancellationToken);
            if (state.Status == "Cancelled" || state.Status == "Failed") throw new InvalidOperationException($"Transfer is {state.Status}.");
            if (state.Status == "Paused")
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }
            if (state.Status != "Transferring") throw new InvalidOperationException($"Transfer is {state.Status}; the receiver must accept it before bytes are uploaded.");
            var remoteItem = state.Items.FirstOrDefault(item => item.ItemId == source.ItemId)
                             ?? throw new InvalidOperationException("The server no longer recognizes this transfer item.");
            if (remoteItem.ReceivedBytes >= source.Size) return;
            var chunkCount = checked((int)Math.Ceiling(source.Size / (double)chunkSize));
            var nextIndex = Math.Min(remoteItem.NextChunkIndex, chunkCount);
            var hadFailure = false;
            long sentSinceSnapshot = 0;

            await using var file = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            for (var index = nextIndex; index < chunkCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = (long)index * chunkSize;
                var wanted = (int)Math.Min(chunkSize, source.Size - offset);
                file.Position = offset;
                var count = 0;
                while (count < wanted)
                {
                    var read = await file.ReadAsync(buffer.AsMemory(count, wanted - count), cancellationToken);
                    if (read == 0) throw new EndOfStreamException($"{source.FileName} changed while it was being sent.");
                    count += read;
                }
                try
                {
                    await using var chunk = new MemoryStream(buffer, 0, count, writable: false, publiclyVisible: true);
                    var chunkStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    await _api.UploadChunkAsync(transferId, source.ItemId, index, chunk, count, cancellationToken);
                    chunkStopwatch.Stop();
                    if (SpeedFactor < 1.0)
                    {
                        var throttleDelay = TimeSpan.FromSeconds(chunkStopwatch.Elapsed.TotalSeconds * (1.0 / SpeedFactor - 1.0));
                        if (throttleDelay > TimeSpan.Zero) await Task.Delay(throttleDelay, cancellationToken);
                    }
                    newlySent += count;
                    sentSinceSnapshot += count;
                    backoff = 1;
                    var elapsed = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                    var speed = newlySent / elapsed;
                    var remain = Math.Max(0, source.Size - remoteItem.ReceivedBytes - sentSinceSnapshot);
                    progress?.Report(new TransferProgress(transferId, source.FileName, source.Size,
                        Math.Min(source.Size, remoteItem.ReceivedBytes + sentSinceSnapshot), speed,
                        speed > 0 ? TimeSpan.FromSeconds(remain / speed) : (TimeSpan?)null, Mode));
                }
                catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized || ex.Message.Contains("Expected chunk", StringComparison.Ordinal))
                {
                    hadFailure = true;
                    break;
                }
                catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    if (ex.Message.Contains("Transfer is paused", StringComparison.OrdinalIgnoreCase))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                        break;
                    }
                    throw;
                }
                catch (Exception ex) when ((ex is HttpRequestException or IOException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
                {
                    hadFailure = true;
                    break;
                }
            }

            if (!hadFailure)
            {
                var latest = await _api.GetTransferProgressAsync(transferId, cancellationToken);
                var latestItem = latest.Items.First(item => item.ItemId == source.ItemId);
                if (latestItem.ReceivedBytes >= source.Size) return;
            }
            if (backoff > 30) throw new IOException($"Unable to reconnect to the Server Relay while sending {source.FileName}.");
            await Task.Delay(TimeSpan.FromSeconds(backoff), cancellationToken);
            backoff = Math.Min(backoff * 2, 30);
        }
    }

    public Task DownloadItemAsync(string transferId, TransferItemDto item, string destinationPath, CancellationToken cancellationToken)
        => _api.DownloadItemAsync(transferId, item, destinationPath, cancellationToken);
}

public sealed class TransferTransportRegistry
{
    private readonly IReadOnlyList<ITransferTransport> _transports;
    public ServerRelayTransport Relay { get; }
    public DirectWifiTransport Direct { get; }

    public P2PTransport P2P { get; }
    public TransferTransportRegistry(ApiClient api)
    {
        Relay = new ServerRelayTransport(api);
        Direct = new DirectWifiTransport(api, Relay);
        P2P = new P2PTransport(api, Relay);
        _transports = [
            P2P,
            Direct,
            Relay,
            new UnavailableTransferTransport(TransferMode.Lightning, "Apple requires the MFi External Accessory entitlement; it is not available to unsigned sideloads."),
        ];
    }

    public IReadOnlyList<ITransferTransport> All => _transports;
    public ITransferTransport SelectAutomatic() => _transports.First(transport => transport.IsAvailable && transport.Mode is TransferMode.P2P or TransferMode.DirectWifi or TransferMode.ServerRelay);
    public ITransferTransport Get(TransferMode mode) => _transports.First(transport => transport.Mode == mode);

    /// <summary>Resolves the user's preferred transport and applies the public-server speed cap.</summary>
    public ITransferTransport SelectPreferred(TransferPreference preference)
    {
        switch (preference)
        {
            case TransferPreference.DirectWifi:
                if (Direct.IsAvailable) return Direct;
                break;
            case TransferPreference.P2P:
                if (P2P.IsAvailable) return P2P;
                break;
            case TransferPreference.ServerApi:
                Relay.SpeedFactor = 0.7;
                return Relay;
        }
        return SelectAutomatic();
    }
}

public sealed class TransferManager(ApiClient api, TransferTransportRegistry transports, Func<TransferPreference>? preferenceAccessor = null)
{
    public async Task<TransferCreateEnvelope> PrepareAsync(string receiverId, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var items = new List<TransferItemRequest>(paths.Count);
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("The selected file no longer exists.", path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);
            items.Add(new TransferItemRequest(info.Name, info.Length, MimeFor(path), Convert.ToHexString(hash).ToLowerInvariant()));
        }
        return await api.CreateTransferAsync(receiverId, items, cancellationToken);
    }

    public async Task<TransferDto> UploadAcceptedAsync(TransferDto transfer, IReadOnlyList<string> sourcePaths,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        if (transfer.Transport != "Server Relay") throw new InvalidOperationException("Transfer transport does not match the authenticated server session.");
        if (sourcePaths.Count != transfer.Items.Count) throw new InvalidOperationException("Selected source files no longer match transfer items.");
        var sources = new List<TransferSource>(sourcePaths.Count);
        for (var index = 0; index < sourcePaths.Count; index++)
        {
            var info = new FileInfo(sourcePaths[index]);
            if (!info.Exists || info.Length != transfer.Items[index].Size) throw new IOException($"Source file changed since the transfer request: {info.Name}");
            sources.Add(new TransferSource(transfer.Items[index].Id, transfer.Items[index].FileName, info.FullName, info.Length));
        }
        var transport = transports.Get(TransferMode.ServerRelay);
        var completedBytes = sources.Sum(source => source.Size);
        var totalBytes = Math.Max(1, completedBytes);
        var doneBytes = 0L;
        foreach (var source in sources)
        {
            var before = doneBytes;
            var sourceProgress = new Progress<TransferProgress>(value =>
            {
                var overall = Math.Clamp((doneBytes + value.TransferredBytes) / (double)totalBytes, 0, 1);
                var speed = value.BytesPerSecond;
                var remaining = speed > 0 ? TimeSpan.FromSeconds((totalBytes - doneBytes - value.TransferredBytes) / speed) : (TimeSpan?)null;
                progress?.Report(new TransferProgress(transfer.Id, value.FileName, totalBytes,
                    (long)(overall * totalBytes), speed, remaining, TransferMode.ServerRelay));
            });
            await transport.UploadItemAsync(transfer.Id, source, transfer.ChunkSize, sourceProgress, cancellationToken);
            doneBytes = before + source.Size;
        }
        return await api.CompleteTransferAsync(transfer.Id, cancellationToken);
    }

    /// <summary>Sender flow that supports the Direct Wi-Fi channel with automatic relay fallback.</summary>
    public async Task<TransferDto> UploadAcceptedWithTransportAsync(TransferDto transfer, IReadOnlyList<string> sourcePaths,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        if (sourcePaths.Count != transfer.Items.Count) throw new InvalidOperationException("Selected source files no longer match transfer items.");
        var sources = new List<TransferSource>(sourcePaths.Count);
        for (var index = 0; index < sourcePaths.Count; index++)
        {
            var info = new FileInfo(sourcePaths[index]);
            if (!info.Exists || info.Length != transfer.Items[index].Size) throw new IOException($"Source file changed since the transfer request: {info.Name}");
            sources.Add(new TransferSource(transfer.Items[index].Id, transfer.Items[index].FileName, info.FullName, info.Length));
        }

        var preference = preferenceAccessor?.Invoke() ?? TransferPreference.Auto;
        if (preference == TransferPreference.ServerApi)
        {
            transports.Relay.SpeedFactor = 0.7;
        }
        else
        {
            // Auto order: P2P (UDP hole punch) -> Direct Wi-Fi (shared LAN) -> Server Relay.
            if ((preference is TransferPreference.Auto or TransferPreference.P2P) && transports.P2P.IsAvailable)
            {
                try
                {
                    var done = await transports.P2P.UploadAcceptedAsync(transfer, sources, progress, cancellationToken);
                    if (done.Status == "Completed" && done.Transport == P2PTransport.TransportName) return done;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                    && (ex is IOException or InvalidOperationException or HttpRequestException or TimeoutException))
                {
                    // punch failed or the peer asked for the relay: continue below
                }
            }
            if ((preference is TransferPreference.Auto or TransferPreference.DirectWifi) && transports.Direct.IsAvailable)
            {
                try
                {
                    var done = await transports.Direct.UploadAcceptedAsync(transfer, sources, progress, cancellationToken);
                    if (done.Status == "Completed" && done.Transport == DirectWifiTransport.TransportName) return done;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                    && (ex is IOException or InvalidOperationException or HttpRequestException))
                {
                    // fall through to the pure relay path below
                }
            }
        }

        var relayTransport = transports.Get(TransferMode.ServerRelay);
        var completedBytes = sources.Sum(source => source.Size);
        var totalBytes = Math.Max(1, completedBytes);
        var doneBytes = 0L;
        foreach (var source in sources)
        {
            var before = doneBytes;
            var sourceProgress = new Progress<TransferProgress>(value =>
            {
                var overall = Math.Clamp((doneBytes + value.TransferredBytes) / (double)totalBytes, 0, 1);
                var speed = value.BytesPerSecond;
                var remaining = speed > 0 ? TimeSpan.FromSeconds((totalBytes - doneBytes - value.TransferredBytes) / speed) : (TimeSpan?)null;
                progress?.Report(new TransferProgress(transfer.Id, value.FileName, totalBytes,
                    (long)(overall * totalBytes), speed, remaining, TransferMode.ServerRelay));
            });
            await relayTransport.UploadItemAsync(transfer.Id, source, transfer.ChunkSize, sourceProgress, cancellationToken);
            doneBytes = before + source.Size;
        }
        return await api.CompleteTransferAsync(transfer.Id, cancellationToken);
    }

    /// <summary>Receiver flow: direct first when a channel exists, relay fallback otherwise.</summary>
    public async Task<IReadOnlyList<string>> DownloadWithTransportAsync(TransferDto transfer, string destinationDirectory,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        // The sender registers its LAN endpoint right after the receiver accepts; give it a short window.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (transfer.DirectInfo is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            transfer = await api.GetTransferAsync(transfer.Id, cancellationToken);
        }
        if (transfer.DirectInfo is not null)
        {
            var direct = (DirectWifiTransport)transports.Get(TransferMode.DirectWifi);
            return await direct.ReceiveAllAsync(transfer, destinationDirectory, progress, cancellationToken);
        }
        // No direct channel: ask the sender to finish over the relay, then download.
        await api.RequestRelayFallbackAsync(transfer.Id, cancellationToken);
        var completed = await WaitForTerminalAsync(transfer.Id, cancellationToken);
        if (completed.Status != "Completed") throw new InvalidOperationException($"Relay fallback ended with status {completed.Status}.");
        var relay = transports.Get(TransferMode.ServerRelay);
        var saved = new List<string>(completed.Items.Count);
        foreach (var item in completed.Items)
        {
            var name = Path.GetFileName(item.FileName);
            if (string.IsNullOrWhiteSpace(name)) name = "received-file";
            var destination = Path.Combine(destinationDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{name}");
            await relay.DownloadItemAsync(transfer.Id, item, destination, cancellationToken);
            saved.Add(destination);
        }
        return saved;
    }

    private async Task<TransferDto> WaitForTerminalAsync(string transferId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddHours(24);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await api.GetTransferAsync(transferId, cancellationToken);
            if (current.Status is "Completed" or "Failed" or "Cancelled") return current;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException("Relay fallback transfer did not finish in time.");
    }

    public Task DownloadAsync(TransferDto transfer, string destinationDirectory, CancellationToken cancellationToken)
        => DownloadCoreAsync(transfer, destinationDirectory, cancellationToken);

    private async Task DownloadCoreAsync(TransferDto transfer, string destinationDirectory, CancellationToken cancellationToken)
    {
        if (transfer.Transport != "Server Relay") throw new InvalidOperationException("Transfer is not an authenticated Server Relay session.");
        Directory.CreateDirectory(destinationDirectory);
        var transport = transports.Get(TransferMode.ServerRelay);
        foreach (var item in transfer.Items)
        {
            var name = Path.GetFileName(item.FileName);
            if (string.IsNullOrWhiteSpace(name)) name = "received-file";
            var destination = Path.Combine(destinationDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{name}");
            await transport.DownloadItemAsync(transfer.Id, item, destination, cancellationToken);
        }
    }

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" => "text/plain", ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        ".zip" => "application/zip", ".json" => "application/json", ".csv" => "text/csv", ".mp4" => "video/mp4",
        ".mp3" => "audio/mpeg", _ => "application/octet-stream"
    };
}
