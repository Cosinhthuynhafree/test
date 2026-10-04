namespace HoshinoTransfer.Windows.Models;

public sealed class UserDto
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";

    public string Initials
    {
        get
        {
            var source = string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;
            var parts = source.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1) return parts[0][..1].ToUpperInvariant();
            return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
        }
    }
}

public sealed class FriendsResponse { public List<FriendDto> Friends { get; set; } = []; }
public sealed class FriendDto
{
    public UserDto User { get; set; } = new();
    public string State { get; set; } = "";
    public string Direction { get; set; } = "";
    public bool Online { get; set; }
}

public sealed class DevicesResponse { public List<DeviceDto> Devices { get; set; } = []; }
public sealed class DeviceEnvelope { public DeviceDto Device { get; set; } = new(); }
public sealed class DeviceDto
{
    public string Id { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Platform { get; set; } = "";
    public bool Online { get; set; }
    public string Status { get; set; } = "Offline";
    public string LastSeen { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

public sealed class ChatsResponse { public List<ChatDto> Chats { get; set; } = []; }
public sealed class ChatEnvelope { public ChatDto Chat { get; set; } = new(); }
public sealed class ChatDto
{
    public string Id { get; set; } = "";
    public UserDto User { get; set; } = new();
    public string? LastMessage { get; set; }
    public bool Online { get; set; }
}
public sealed class MessagesResponse { public List<MessageDto> Messages { get; set; } = []; }
public sealed class MessageEnvelope { public MessageDto Message { get; set; } = new(); }
public sealed class MessageDto
{
    public string Id { get; set; } = "";
    public string SenderId { get; set; } = "";
    public string Content { get; set; } = "";
    public string? TransferId { get; set; }
    public string Timestamp { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsOwn { get; set; }
}

public sealed class PeerCandidateInfo
{
    public string Host { get; set; } = "";
    public int? Port { get; set; }
    public string? LanHost { get; set; }
    public int? LanPort { get; set; }
}

public sealed class DirectEndpointInfo
{
    public string Host { get; set; } = "";
    public int? Port { get; set; }
    public string? Token { get; set; }
}

public sealed class TransfersResponse { public List<TransferDto> Transfers { get; set; } = []; }
public sealed class TransferEnvelope { public TransferDto Transfer { get; set; } = new(); }
public sealed class TransferCreateEnvelope
{
    public TransferDto Transfer { get; set; } = new();
    public int ChunkSize { get; set; }
    public string Transport { get; set; } = "";
}
public sealed class TransferDto
{
    public string Id { get; set; } = "";
    public string SenderId { get; set; } = "";
    public string ReceiverId { get; set; } = "";
    public string Status { get; set; } = "";
    public string Transport { get; set; } = "";
    public string ExpiresAt { get; set; } = "";
    public int ChunkSize { get; set; } = 4 * 1024 * 1024;
    public DirectEndpointInfo? DirectInfo { get; set; }
    public bool RelayRequested { get; set; }
    public List<PeerCandidateInfo> PeerCandidates { get; set; } = [];
    public List<TransferItemDto> Items { get; set; } = [];
}
public sealed class TransferItemDto
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string MimeType { get; set; } = "application/octet-stream";
    public string Sha256 { get; set; } = "";
    public string Status { get; set; } = "";
    public bool DuplicateDetected { get; set; }
    public long ReceivedBytes { get; set; }
    public List<int> ReceivedChunks { get; set; } = [];
    public int NextChunkIndex { get; set; }
}
