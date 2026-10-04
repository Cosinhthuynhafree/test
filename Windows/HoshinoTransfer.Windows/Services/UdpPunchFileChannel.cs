using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using HoshinoTransfer.Windows.Models;

namespace HoshinoTransfer.Windows.Services;

/// <summary>
/// UDP hole-punch file channel. Wire format: [type:1][payload]
/// 0x00 PING(8) · 0x01 PONG(8) · 0x02 REQ(itemIndex:4,segIndex:4) · 0x03 DATA(itemIndex:4,segIndex:4,payload)
/// Segments are 60,000 bytes; the receiver drives requests with retries, so the
/// channel is reliable without any congestion-control machinery.
/// </summary>
public static class UdpPunchFileChannel
{
    public const int SegmentSize = 60000;
    private const byte Ping = 0x00, Pong = 0x01, Req = 0x02, Data = 0x03;

    public readonly record struct Candidate(string Host, int Port)
    {
        public IPEndPoint ToEndPoint() => new(IPAddress.Parse(Host), Port);
    }

    public static IEnumerable<Candidate> ParseCandidates(IReadOnlyList<PeerCandidateInfo> candidates)
    {
        foreach (var entry in candidates)
        {
            if (entry.Host is not null && IPAddress.TryParse(entry.Host, out _) && entry.Port is int port && port > 0)
                yield return new Candidate(entry.Host, port);
            if (entry.LanHost is not null && IPAddress.TryParse(entry.LanHost, out _) && entry.LanPort is int lanPort && lanPort > 0)
                yield return new Candidate(entry.LanHost, lanPort);
        }
    }

    /// <summary>Both sides exchange PING/PONG until one datagram arrives; the remote is then locked.</summary>
    public static async Task<IPEndPoint> EstablishAsync(UdpClient socket, IEnumerable<Candidate> candidates, CancellationToken cancellationToken)
    {
        var targets = candidates.Select(entry => entry.ToEndPoint()).Distinct().ToArray();
        var pingPayload = RandomNumberGenerator.GetBytes(8);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
        using var timer = new System.Threading.PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var receiveTask = ReceiveFirstAsync(socket, pingPayload, cancellationToken);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var target in targets)
            {
                var ping = new byte[9];
                ping[0] = Ping;
                Array.Copy(pingPayload, 0, ping, 1, 8);
                await socket.SendAsync(ping, ping.Length, target);
            }
            var finished = await Task.WhenAny(receiveTask, timer.WaitForNextTickAsync(cancellationToken).AsTask());
            if (finished == receiveTask) return receiveTask.Result;
        }
        throw new TimeoutException("UDP hole punching did not open a channel in time.");
    }

    private static async Task<IPEndPoint> ReceiveFirstAsync(UdpClient socket, byte[] pingPayload, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remote = new IPEndPoint(IPAddress.Any, 0);
            var datagram = await socket.ReceiveAsync(ct);
            remote = datagram.RemoteEndPoint;
            var data = datagram.Buffer;
            if (data.Length >= 9 && (data[0] == Ping || data[0] == Pong || data[0] == Req || data[0] == Data))
            {
                if (data[0] == Ping)
                {
                    var pong = new byte[9];
                    pong[0] = Pong;
                    Array.Copy(data, 1, pong, 1, 8);
                    await socket.SendAsync(pong, pong.Length, remote);
                }
                return remote;
            }
        }
    }

    /// <summary>Sender side: answer segment requests with file slices until cancelled.</summary>
    public static async Task ServeAsync(UdpClient socket, IPEndPoint peer, IReadOnlyList<TransferSource> sources, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            (int ItemIndex, int SegmentIndex)? request;
            try
            {
                var result = await socket.ReceiveAsync(ct);
                if (!result.RemoteEndPoint.Equals(peer)) continue;
                request = ParseHeader(result.Buffer, Req);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            if (request is null) continue;

            var (itemIndex, segmentIndex) = request.Value;
            if (itemIndex < 0 || itemIndex >= sources.Count) continue;
            var source = sources[itemIndex];
            var offset = (long)segmentIndex * SegmentSize;
            if (offset >= source.Size) continue;
            var length = (int)Math.Min(SegmentSize, source.Size - offset);

            var packet = new byte[9 + length];
            packet[0] = Data;
            WriteInt(packet, 1, itemIndex);
            WriteInt(packet, 5, segmentIndex);
            try
            {
                await using var file = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
                file.Position = offset;
                var read = 0;
                while (read < length)
                {
                    var got = await file.ReadAsync(packet.AsMemory(9 + read, length - read), ct);
                    if (got == 0) throw new EndOfStreamException(source.FileName);
                    read += got;
                }
                await socket.SendAsync(packet, packet.Length, peer);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* transient IO: the receiver will re-request */ }
        }
    }

    /// <summary>Receiver side: request every segment with retries, assemble, verify SHA-256 per item.</summary>
    public static async Task<IReadOnlyList<string>> ReceiveAllAsync(UdpClient socket, IPEndPoint peer,
        IReadOnlyList<(TransferItemDto Item, TransferSource Source)> items, string destinationDirectory,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destinationDirectory);
        var saved = new List<string>(items.Count);
        var totalBytes = items.Sum(entry => entry.Item.Size);
        var doneBytes = 0L;
        for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
        {
            var (item, source) = items[itemIndex];
            var name = Path.GetFileName(item.FileName);
            if (string.IsNullOrWhiteSpace(name)) name = "received-file";
            var destination = Path.Combine(destinationDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{name}");
            var segmentCount = checked((int)Math.Ceiling(item.Size / (double)SegmentSize));
            var received = new bool[segmentCount];
            var buffer = new byte[SegmentSize];
            var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);

            var missing = new SortedSet<int>(Enumerable.Range(0, segmentCount));
            var pending = new Dictionary<int, DateTimeOffset>();
            const int window = 24;
            while (missing.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var segment in missing.Take(window))
                {
                    if (pending.TryGetValue(segment, out var sentAt) && DateTimeOffset.UtcNow - sentAt < TimeSpan.FromMilliseconds(800)) continue;
                    var request = new byte[9];
                    request[0] = Req;
                    WriteInt(request, 1, itemIndex);
                    WriteInt(request, 5, segment);
                    await socket.SendAsync(request, request.Length, peer);
                    pending[segment] = DateTimeOffset.UtcNow;
                }

                var datagram = await ReceiveWithTimeoutAsync(socket, peer, TimeSpan.FromSeconds(1), ct);
                if (datagram is null) continue;
                var header = ParseHeader(datagram.Value.Buffer, Data);
                if (header is null) continue;
                var (dataItem, dataSegment) = header.Value;
                if (dataItem != itemIndex || dataSegment >= segmentCount || received[dataSegment]) continue;
                received[dataSegment] = true;
                var payload = new byte[datagram.Value.Buffer.Length - 9];
                Array.Copy(datagram.Value.Buffer, 9, payload, 0, payload.Length);
                await output.WriteAsync(payload, ct);
                hash.AppendData(payload);
                missing.Remove(dataSegment);
                pending.Remove(dataSegment);
                doneBytes += payload.Length;
                progress?.Report(new TransferProgress("p2p", item.FileName, totalBytes, doneBytes, 0, null, TransferMode.P2P));
            }

            var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(digest), Convert.FromHexString(item.Sha256)))
            {
                output.Close();
                File.Delete(destination);
                throw new IOException($"P2P transfer of {item.FileName} failed SHA-256 verification.");
            }
            saved.Add(destination);
        }
        return saved;
    }

    private static async Task<(byte[] Buffer, IPEndPoint Remote)?> ReceiveWithTimeoutAsync(UdpClient socket, IPEndPoint peer, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var result = await socket.ReceiveAsync(timeoutCts.Token);
            return result.RemoteEndPoint.Equals(peer) ? (result.Buffer, result.RemoteEndPoint) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private static (int ItemIndex, int SegmentIndex)? ParseHeader(byte[] buffer, byte expectedType)
    {
        if (buffer.Length < 9 || buffer[0] != expectedType) return null;
        return (ReadInt(buffer, 1), ReadInt(buffer, 5));
    }

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static int ReadInt(byte[] buffer, int offset)
        => (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
}
