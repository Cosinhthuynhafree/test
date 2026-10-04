using System.Net;
using System.Net.Sockets;
using System.IO;
using HoshinoTransfer.Windows.Models;

namespace HoshinoTransfer.Windows.Services;

/// <summary>
/// Cross-network peer-to-peer transport: STUN gives each side its public UDP
/// mapping, the server relays candidates, and UDP hole punching opens a direct
/// channel. Falls back automatically when punching fails or the transfer errors.
/// </summary>
public sealed class P2PTransport : ITransferTransport
{
    public const string TransportName = "P2P (UDP)";
    private readonly ApiClient _api;
    private readonly ServerRelayTransport _relay;

    public P2PTransport(ApiClient api, ServerRelayTransport relay)
    {
        _api = api;
        _relay = relay;
    }

    public TransferMode Mode => TransferMode.P2P;
    public bool IsAvailable => LanTransferListener.GetLocalIPv4() is not null;
    public string AvailabilityReason => IsAvailable
        ? "UDP hole punching via public STUN; falls back to the relay for symmetric NATs."
        : "No local network interface is available for UDP.";

    public Task UploadItemAsync(string transferId, TransferSource source, int chunkSize, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException("The P2P sender flow is orchestrated per transfer."));

    public Task DownloadItemAsync(string transferId, TransferItemDto item, string destinationPath, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException("The P2P receiver flow is orchestrated per transfer."));

    /// <summary>Sender: announce candidates, serve segment requests, react to terminal states.</summary>
    public async Task<TransferDto> UploadAcceptedAsync(TransferDto transfer, IReadOnlyList<TransferSource> sources,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        var socket = StunClient.Bind(cancellationToken)
            ?.Socket ?? throw new InvalidOperationException("STUN binding failed; no UDP path is available.");
        var publicEndpoint = StunClient.LastPublicEndpoint!;
        var lanIp = LanTransferListener.GetLocalIPv4() ?? "127.0.0.1";
        var lanPort = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        await _api.RegisterCandidatesAsync(transfer.Id, publicEndpoint.Address.ToString(), publicEndpoint.Port, lanIp, lanPort, cancellationToken);
        await using var registration = new CancellationTokenRegistration();
        var peerCandidates = (await _api.GetTransferAsync(transfer.Id, cancellationToken)).PeerCandidates;

        var channel = UdpPunchFileChannel.EstablishAsync(socket, UdpPunchFileChannel.ParseCandidates(peerCandidates).ToList(), cancellationToken);
        var serveTask = Task.Run(async () =>
        {
            try
            {
                var peer = await channel.WaitAsync(cancellationToken);
                await UdpPunchFileChannel.ServeAsync(socket, peer, sources, cancellationToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }, CancellationToken.None);

        TransferDto? terminal = null;
        var started = DateTimeOffset.UtcNow;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = await _api.GetTransferAsync(transfer.Id, cancellationToken);
                if (state.Status is "Completed" or "Failed" or "Cancelled") { terminal = state; break; }
                if (state.RelayRequested || DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(20)) break;
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
        finally
        {
            socket.Dispose();
            try { await serveTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }

        if (terminal is { Status: "Completed" }) return terminal;

        foreach (var source in sources)
            await _relay.UploadItemAsync(transfer.Id, source, transfer.ChunkSize, progress, cancellationToken);
        return await _api.CompleteTransferAsync(transfer.Id, cancellationToken);
    }

    /// <summary>Receiver: punch, pull all segments, verify, then complete on the server.</summary>
    public async Task<IReadOnlyList<string>> ReceiveAllAsync(TransferDto transfer, IReadOnlyList<TransferSource> mappedSources,
        string destinationDirectory, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        var socket = StunClient.Bind(cancellationToken)
            ?.Socket ?? throw new InvalidOperationException("STUN binding failed; no UDP path is available.");
        var publicEndpoint = StunClient.LastPublicEndpoint!;
        var lanIp = LanTransferListener.GetLocalIPv4() ?? "127.0.0.1";
        var lanPort = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        await _api.RegisterCandidatesAsync(transfer.Id, publicEndpoint.Address.ToString(), publicEndpoint.Port, lanIp, lanPort, cancellationToken);
        var peerCandidates = (await _api.GetTransferAsync(transfer.Id, cancellationToken)).PeerCandidates;

        var channel = UdpPunchFileChannel.EstablishAsync(socket, UdpPunchFileChannel.ParseCandidates(peerCandidates).ToList(), cancellationToken);
        var peer = await channel.WaitAsync(cancellationToken);

        var pairs = mappedSources.Select((source, index) => (transfer.Items[index], source)).ToList();
        return await UdpPunchFileChannel.ReceiveAllAsync(socket, peer, pairs, destinationDirectory, progress, cancellationToken);
    }
}
