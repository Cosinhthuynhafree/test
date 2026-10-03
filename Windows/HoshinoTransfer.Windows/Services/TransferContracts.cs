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

public sealed class ServerRelayTransport(ApiClient api) : ITransferTransport
{
    public TransferMode Mode => TransferMode.ServerRelay;
    public bool IsAvailable => true;
    public string AvailabilityReason => "Authenticated HTTPS chunk relay is available.";

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
            var state = await api.GetTransferProgressAsync(transferId, cancellationToken);
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
                    await api.UploadChunkAsync(transferId, source.ItemId, index, chunk, count, cancellationToken);
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
                var latest = await api.GetTransferProgressAsync(transferId, cancellationToken);
                var latestItem = latest.Items.First(item => item.ItemId == source.ItemId);
                if (latestItem.ReceivedBytes >= source.Size) return;
            }
            if (backoff > 30) throw new IOException($"Unable to reconnect to the Server Relay while sending {source.FileName}.");
            await Task.Delay(TimeSpan.FromSeconds(backoff), cancellationToken);
            backoff = Math.Min(backoff * 2, 30);
        }
    }

    public Task DownloadItemAsync(string transferId, TransferItemDto item, string destinationPath, CancellationToken cancellationToken)
        => api.DownloadItemAsync(transferId, item, destinationPath, cancellationToken);
}

public sealed class TransferTransportRegistry
{
    private readonly IReadOnlyList<ITransferTransport> _transports;
    public TransferTransportRegistry(ApiClient api)
    {
        _transports = [
            new UnavailableTransferTransport(TransferMode.P2P, "No ICE/STUN/TURN peer transport is configured."),
            new UnavailableTransferTransport(TransferMode.DirectWifi, "No local discovery or authenticated direct socket adapter is configured."),
            new ServerRelayTransport(api),
            new UnavailableTransferTransport(TransferMode.Lightning, "No supported iOS cable transfer API is available to this Windows client."),
        ];
    }

    public IReadOnlyList<ITransferTransport> All => _transports;
    public ITransferTransport SelectAutomatic() => _transports.First(transport => transport.IsAvailable && transport.Mode is TransferMode.P2P or TransferMode.DirectWifi or TransferMode.ServerRelay);
    public ITransferTransport Get(TransferMode mode) => _transports.First(transport => transport.Mode == mode);
}

public sealed class TransferManager(ApiClient api, TransferTransportRegistry transports)
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
        var uploadStopwatch = System.Diagnostics.Stopwatch.StartNew();
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
