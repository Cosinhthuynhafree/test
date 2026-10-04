using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace HoshinoTransfer.Windows.Services;

/// <summary>Minimal RFC 5389 STUN binding client (XOR-MAPPED-ADDRESS only).</summary>
public static class StunClient
{
    private static readonly (string Host, int Port)[] Servers =
    [
        ("stun.l.google.com", 19302),
        ("stun.cloudflare.com", 3478),
    ];

    public static IPEndPoint? LastPublicEndpoint { get; private set; }

    public static (IPEndPoint Public, UdpClient Socket)? Bind(CancellationToken cancellationToken)
    {
        foreach (var (host, port) in Servers)
        {
            try
            {
                var socket = new UdpClient(0);
                try
                {
                    var addresses = Dns.GetHostAddressesAsync(host, System.Net.Sockets.AddressFamily.InterNetwork, cancellationToken).WaitAsync(TimeSpan.FromSeconds(4), cancellationToken).Result;
                    if (addresses.Length == 0) { socket.Dispose(); continue; }
                    var endpoint = new IPEndPoint(addresses[0], port);
                    var transactionId = RandomNumberGenerator.GetBytes(12);
                    var packet = BuildBindingRequest(transactionId);
                    socket.Client.ReceiveTimeout = 3000;
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        socket.Send(packet, packet.Length, endpoint);
                        try
                        {
                            while (true)
                            {
                                var remote = new IPEndPoint(IPAddress.Any, 0);
                                var response = socket.Receive(ref remote);
                                var mapped = ParseXorMappedAddress(response, transactionId);
                                if (mapped is null) continue;
                                LastPublicEndpoint = mapped;
                                return (mapped, socket);
                            }
                        }
                        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { }
                    }
                }
                catch { }
                socket.Dispose();
            }
            catch { }
        }
        return null;
    }

    private static byte[] BuildBindingRequest(byte[] transactionId)
    {
        var packet = new byte[20];
        packet[0] = 0x00; packet[1] = 0x01; // Binding request
        packet[2] = 0x00; packet[3] = 0x00; // message length
        packet[4] = 0x21; packet[5] = 0x12; packet[6] = 0xA4; packet[7] = 0x42; // magic cookie
        Array.Copy(transactionId, 0, packet, 8, 12);
        return packet;
    }

    private static IPEndPoint? ParseXorMappedAddress(byte[] response, byte[] transactionId)
    {
        if (response.Length < 20) return null;
        var index = 20;
        while (index + 4 <= response.Length)
        {
            var type = (response[index] << 8) | response[index + 1];
            var length = (response[index + 2] << 8) | response[index + 3];
            if (type == 0x0020 && length >= 8 && index + 4 + length <= response.Length)
            {
                var port = ((response[index + 6] << 8) | response[index + 7]) ^ 0x2112;
                var ip = new byte[4];
                for (var i = 0; i < 4; i++) ip[i] = (byte)(response[index + 8 + i] ^ (i < 4 ? 0x21 : 0x00));
                ip[0] ^= 0x21; ip[1] ^= 0x12; ip[2] ^= 0xA4; ip[3] ^= 0x42;
                return new IPEndPoint(new IPAddress(ip), port);
            }
            index += 4 + length;
        }
        return null;
    }
}
