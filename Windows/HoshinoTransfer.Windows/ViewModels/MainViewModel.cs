using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using HoshinoTransfer.Windows.Models;
using HoshinoTransfer.Windows.Services;
using Microsoft.Win32;

namespace HoshinoTransfer.Windows.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ApiClient _api;
    private readonly TransferTransportRegistry _transportRegistry;
    private readonly TransferManager _transferManager;
    private readonly TransferSourceStore _sourceStore = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _eventLifetime;
    private string _username = "";
    private string _displayName = "";
    private string _password = "";
    private string _friendUsername = "";
    private string _searchQuery = "";
    private string _pairingCode = "";
    private string _messageDraft = "";
    private string _transferSpeed = "—";
    private string _transferEta = "—";
    private double _transferPercent;
    private string _typingStatus = "";
    private DeviceDto? _selectedDevice;
    private UserDto? _selectedSearchUser;
    private string _deviceName = Environment.MachineName;
    private bool _isRegisterMode;
    private bool _isAuthenticated;
    private bool _isBusy;
    private bool _isSending;
    private string _errorMessage = "";
    private string _noticeMessage = "";
    private string _page = "Home";
    private string _pageDescription = "Your devices and recent activity, in one quiet place.";
    private string _backendStatus = "Checking secure connection…";
    private string _signedInName = "";
    private string _currentChatId = "";
    private string _currentUserId = "";
    private string _currentPeerId = "";
    private string _currentPeerName = "";
    private string _selectedTransferId = "";
    private FriendDto? _selectedFriend;
    private TransferDto? _selectedIncomingTransfer;
    private readonly Dictionary<string, UserDto> _knownUsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource<string>> _transferWaiters = new(StringComparer.Ordinal);

    public MainViewModel(ApiClient api)
    {
        _api = api;
        _transportRegistry = new TransferTransportRegistry(api);
        _transferManager = new TransferManager(api, _transportRegistry);
        SubmitAuthCommand = new AsyncRelayCommand(SubmitAuthAsync, () => !IsBusy);
        NavigateCommand = new RelayCommand<string>(Navigate);
        ToggleAuthModeCommand = new RelayCommand(() => { IsRegisterMode = !IsRegisterMode; ErrorMessage = ""; });
        LogoutCommand = new AsyncRelayCommand(LogoutAsync, () => !IsBusy);
        RefreshBackendCommand = new AsyncRelayCommand(RefreshBackendAsync, () => !IsBusy);
        RefreshDataCommand = new AsyncRelayCommand(RefreshDataAsync, () => IsAuthenticated && !IsBusy);
        RequestFriendCommand = new AsyncRelayCommand(RequestFriendAsync, () => !IsBusy);
        SearchUsersCommand = new AsyncRelayCommand(SearchUsersAsync, () => !IsBusy && SearchQuery.Trim().Length >= 3);
        AddSearchResultCommand = new AsyncRelayCommand(AddSelectedSearchUserAsync, () => !IsBusy && SelectedSearchUser is not null);
        AcceptFriendCommand = new AsyncRelayCommand(() => RespondFriendAsync(true), () => !IsBusy && SelectedFriend is not null);
        RejectFriendCommand = new AsyncRelayCommand(() => RespondFriendAsync(false), () => !IsBusy && SelectedFriend is not null);
        RemoveFriendCommand = new AsyncRelayCommand(() => RemoveFriendAsync(false), () => !IsBusy && SelectedFriend is not null);
        BlockFriendCommand = new AsyncRelayCommand(() => RemoveFriendAsync(true), () => !IsBusy && SelectedFriend is not null);
        UnblockFriendCommand = new AsyncRelayCommand(UnblockSelectedFriendAsync, () => !IsBusy && SelectedFriend is not null && SelectedFriend.State == "Blocked");
        SelectFriendCommand = new AsyncRelayCommand(OpenChatAsync, () => SelectedFriend is not null && !IsBusy);
        SendMessageCommand = new AsyncRelayCommand(SendMessageAsync, () => !IsSending && !string.IsNullOrWhiteSpace(MessageDraft));
        RegisterDeviceCommand = new AsyncRelayCommand(RegisterDeviceAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(DeviceName));
        CreatePairingCodeCommand = new AsyncRelayCommand(CreatePairingCodeAsync, () => !IsBusy);
        PairDeviceCommand = new AsyncRelayCommand(PairDeviceAsync, () => !IsBusy && PairingCode.Length == 8);
        RevokeDeviceCommand = new AsyncRelayCommand(RevokeSelectedDeviceAsync, () => !IsBusy && SelectedDevice is not null);
        OpenFilesCommand = new RelayCommand(OpenFiles);
        OpenChatAttachmentCommand = new RelayCommand(OpenChatAttachment);
        AcceptTransferCommand = new AsyncRelayCommand(() => HandleIncomingTransferAsync(true), () => SelectedIncomingTransfer is not null && !IsBusy);
        DeclineTransferCommand = new AsyncRelayCommand(() => HandleIncomingTransferAsync(false), () => SelectedIncomingTransfer is not null && !IsBusy);
        CancelTransferCommand = new AsyncRelayCommand(() => TransferActionAsync("cancel"), () => !IsBusy && !string.IsNullOrEmpty(SelectedTransferId));
        PauseTransferCommand = new AsyncRelayCommand(() => TransferActionAsync("pause"), () => !IsBusy && !string.IsNullOrEmpty(SelectedTransferId));
        ResumeTransferCommand = new AsyncRelayCommand(() => TransferActionAsync("resume"), () => !IsBusy && !string.IsNullOrEmpty(SelectedTransferId));
        RetryTransferCommand = new AsyncRelayCommand(RetrySelectedTransferAsync, () => !IsBusy && !string.IsNullOrEmpty(SelectedTransferId));
        _ = RefreshBackendAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? PasswordCleared;
    public ObservableCollection<FriendDto> Friends { get; } = [];
    public ObservableCollection<DeviceDto> Devices { get; } = [];
    public ObservableCollection<ChatDto> Chats { get; } = [];
    public ObservableCollection<MessageDto> Messages { get; } = [];
    public ObservableCollection<TransferDto> Transfers { get; } = [];
    public ObservableCollection<TransferItemDto> PendingTransferItems { get; } = [];
    public ObservableCollection<UserDto> SearchResults { get; } = [];
    public ObservableCollection<string> TransferLogs { get; } = [];

    public string Username { get => _username; set => Set(ref _username, value); }
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public string Password { get => _password; set => Set(ref _password, value); }
    public string FriendUsername { get => _friendUsername; set => Set(ref _friendUsername, value); }
    public string SearchQuery { get => _searchQuery; set { if (Set(ref _searchQuery, value)) SearchUsersCommand.NotifyCanExecuteChanged(); } }
    public string PairingCode { get => _pairingCode; set { if (Set(ref _pairingCode, value)) PairDeviceCommand.NotifyCanExecuteChanged(); } }
    public string MessageDraft { get => _messageDraft; set { if (Set(ref _messageDraft, value)) SendMessageCommand.NotifyCanExecuteChanged(); } }
    public string DeviceName { get => _deviceName; set => Set(ref _deviceName, value); }
    public bool IsRegisterMode { get => _isRegisterMode; set { if (Set(ref _isRegisterMode, value)) { OnPropertyChanged(nameof(AuthActionLabel)); OnPropertyChanged(nameof(AuthModePrompt)); } } }
    public bool IsAuthenticated { get => _isAuthenticated; private set => Set(ref _isAuthenticated, value); }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) { SubmitAuthCommand.NotifyCanExecuteChanged(); LogoutCommand.NotifyCanExecuteChanged(); RefreshBackendCommand.NotifyCanExecuteChanged(); RefreshDataCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(BusyLabel)); } } }
    public bool IsSending { get => _isSending; private set { if (Set(ref _isSending, value)) SendMessageCommand.NotifyCanExecuteChanged(); } }
    public string ErrorMessage { get => _errorMessage; private set => Set(ref _errorMessage, value); }
    public string NoticeMessage { get => _noticeMessage; private set => Set(ref _noticeMessage, value); }
    public string Page { get => _page; private set => Set(ref _page, value); }
    public string PageDescription { get => _pageDescription; private set => Set(ref _pageDescription, value); }
    public string BackendStatus { get => _backendStatus; private set => Set(ref _backendStatus, value); }
    public string SignedInName { get => _signedInName; private set => Set(ref _signedInName, value); }
    public string TransferSpeed { get => _transferSpeed; private set => Set(ref _transferSpeed, value); }
    public string TransferEta { get => _transferEta; private set => Set(ref _transferEta, value); }
    public double TransferPercent { get => _transferPercent; private set => Set(ref _transferPercent, value); }
    public string TypingStatus { get => _typingStatus; private set => Set(ref _typingStatus, value); }
    public string CurrentChatId { get => _currentChatId; private set => Set(ref _currentChatId, value); }
    public string CurrentPeerName { get => _currentPeerName; private set => Set(ref _currentPeerName, value); }
    public string SelectedTransferId { get => _selectedTransferId; set { if (Set(ref _selectedTransferId, value)) { CancelTransferCommand.NotifyCanExecuteChanged(); PauseTransferCommand.NotifyCanExecuteChanged(); ResumeTransferCommand.NotifyCanExecuteChanged(); RetryTransferCommand.NotifyCanExecuteChanged(); } } }
    public DeviceDto? SelectedDevice { get => _selectedDevice; set { if (Set(ref _selectedDevice, value)) RevokeDeviceCommand.NotifyCanExecuteChanged(); } }
    public UserDto? SelectedSearchUser { get => _selectedSearchUser; set { if (Set(ref _selectedSearchUser, value)) AddSearchResultCommand.NotifyCanExecuteChanged(); } }
    public FriendDto? SelectedFriend
    {
        get => _selectedFriend;
        set
        {
            if (!Set(ref _selectedFriend, value)) return;
            AcceptFriendCommand.NotifyCanExecuteChanged(); RejectFriendCommand.NotifyCanExecuteChanged();
            RemoveFriendCommand.NotifyCanExecuteChanged(); BlockFriendCommand.NotifyCanExecuteChanged(); UnblockFriendCommand.NotifyCanExecuteChanged(); SelectFriendCommand.NotifyCanExecuteChanged();
        }
    }
    public TransferDto? SelectedIncomingTransfer
    {
        get => _selectedIncomingTransfer;
        set
        {
            if (!Set(ref _selectedIncomingTransfer, value)) return;
            AcceptTransferCommand.NotifyCanExecuteChanged(); DeclineTransferCommand.NotifyCanExecuteChanged();
        }
    }
    public string AuthActionLabel => IsRegisterMode ? "Create account" : "Sign in";
    public string AuthModePrompt => IsRegisterMode ? "Already have an account? Sign in" : "New to HoshinoTransfer? Create an account";
    public string BusyLabel => IsBusy ? "Connecting securely…" : AuthActionLabel;

    public AsyncRelayCommand SubmitAuthCommand { get; }
    public RelayCommand<string> NavigateCommand { get; }
    public ICommand ToggleAuthModeCommand { get; }
    public AsyncRelayCommand LogoutCommand { get; }
    public AsyncRelayCommand RefreshBackendCommand { get; }
    public AsyncRelayCommand RefreshDataCommand { get; }
    public AsyncRelayCommand RequestFriendCommand { get; }
    public AsyncRelayCommand SearchUsersCommand { get; }
    public AsyncRelayCommand AddSearchResultCommand { get; }
    public AsyncRelayCommand AcceptFriendCommand { get; }
    public AsyncRelayCommand RejectFriendCommand { get; }
    public AsyncRelayCommand RemoveFriendCommand { get; }
    public AsyncRelayCommand BlockFriendCommand { get; }
    public AsyncRelayCommand UnblockFriendCommand { get; }
    public AsyncRelayCommand SelectFriendCommand { get; }
    public AsyncRelayCommand SendMessageCommand { get; }
    public AsyncRelayCommand RegisterDeviceCommand { get; }
    public AsyncRelayCommand CreatePairingCodeCommand { get; }
    public AsyncRelayCommand PairDeviceCommand { get; }
    public AsyncRelayCommand RevokeDeviceCommand { get; }
    public RelayCommand OpenFilesCommand { get; }
    public RelayCommand OpenChatAttachmentCommand { get; }
    public AsyncRelayCommand AcceptTransferCommand { get; }
    public AsyncRelayCommand DeclineTransferCommand { get; }
    public AsyncRelayCommand CancelTransferCommand { get; }
    public AsyncRelayCommand PauseTransferCommand { get; }
    public AsyncRelayCommand ResumeTransferCommand { get; }
    public AsyncRelayCommand RetryTransferCommand { get; }

    private async Task SubmitAuthAsync()
    {
        ErrorMessage = "";
        NoticeMessage = "";
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password)) { ErrorMessage = "Enter your username and password."; return; }
        if (IsRegisterMode && string.IsNullOrWhiteSpace(DisplayName)) { ErrorMessage = "Enter a display name to create your account."; return; }
        IsBusy = true;
        try
        {
            var result = IsRegisterMode ? await _api.RegisterAsync(Username.Trim(), DisplayName.Trim(), Password, _lifetime.Token) : await _api.LoginAsync(Username.Trim(), Password, _lifetime.Token);
            _currentUserId = result.Id;
            SignedInName = string.IsNullOrWhiteSpace(result.DisplayName) ? result.Username : result.DisplayName;
            Password = "";
            PasswordCleared?.Invoke();
            IsAuthenticated = true;
            Navigate("Home");
            BackendStatus = $"Connected securely · {new Uri(_api.BaseAddress, "api/health").Host}";
            await EnsureDeviceRegisteredAsync();
            StartEventStream();
            await RefreshDataAsync();
        }
        catch (ApiException ex) { ErrorMessage = ex.Message; }
        catch (HttpRequestException) { ErrorMessage = "Unable to connect to the service. Check your internet connection and try again."; }
        catch (TaskCanceledException) { ErrorMessage = "The request timed out. The server may be unavailable."; }
        finally { IsBusy = false; }
    }

    public async Task TryRestoreSessionAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _api.RestoreSessionAsync(_lifetime.Token);
            if (result is null) return;
            _currentUserId = result.Id;
            SignedInName = string.IsNullOrWhiteSpace(result.DisplayName) ? result.Username : result.DisplayName;
            IsAuthenticated = true;
            Navigate("Home");
            await EnsureDeviceRegisteredAsync();
            StartEventStream();
            await RefreshDataAsync();
        }
        finally { IsBusy = false; }
    }

    private async Task EnsureDeviceRegisteredAsync()
    {
        if (!string.IsNullOrWhiteSpace(_api.DeviceId))
        {
            try { await _api.HeartbeatDeviceAsync(_lifetime.Token); return; }
            catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        }
        var device = await _api.RegisterDeviceAsync(DeviceName, "Windows", _lifetime.Token);
        NoticeMessage = $"Registered this Windows device: {device.DeviceName}.";
    }

    private async Task LogoutAsync()
    {
        IsBusy = true;
        _eventLifetime?.Cancel();
        try { await _api.LogoutAsync(_lifetime.Token); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ApiException) { NoticeMessage = "Signed out locally. The service could not be reached to revoke the session."; }
        finally
        {
            _eventLifetime?.Dispose(); _eventLifetime = null;
            IsAuthenticated = false; SignedInName = ""; Password = ""; _currentUserId = "";
            PasswordCleared?.Invoke();
            Friends.Clear(); Devices.Clear(); Chats.Clear(); Messages.Clear(); Transfers.Clear();
            IsBusy = false;
        }
    }

    private async Task RefreshBackendAsync()
    {
        try { BackendStatus = await _api.CheckHealthAsync(_lifetime.Token) ? $"Secure service available · {_api.BaseAddress.Host}" : "Service returned an error. Retry when ready."; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { BackendStatus = "Service unreachable · check your connection"; }
    }

    private async Task RefreshDataAsync()
    {
        if (!IsAuthenticated) return;
        try
        {
            var friends = await _api.GetFriendsAsync(_lifetime.Token);
            Friends.Clear();
            foreach (var friend in friends.Friends) { Friends.Add(friend); _knownUsers[friend.User.Id] = friend.User; }
            var devices = await _api.GetDevicesAsync(_lifetime.Token);
            Devices.Clear(); foreach (var device in devices.Devices) Devices.Add(device);
            var chats = await _api.GetChatsAsync(_lifetime.Token);
            Chats.Clear(); foreach (var chat in chats.Chats) Chats.Add(chat);
            var transfers = await _api.GetTransfersAsync(_lifetime.Token);
            Transfers.Clear(); foreach (var transfer in transfers.Transfers) Transfers.Add(transfer);
            if (SelectedIncomingTransfer is null || SelectedIncomingTransfer.Status != "Pending")
                SelectedIncomingTransfer = transfers.Transfers.FirstOrDefault(transfer => transfer.ReceiverId == _currentUserId && transfer.Status == "Pending");
            BackendStatus = $"Connected · {_api.BaseAddress.Host}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ApiException) { NoticeMessage = ex.Message; }
    }

    private void StartEventStream()
    {
        _eventLifetime?.Cancel(); _eventLifetime?.Dispose();
        _eventLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _eventLifetime.Token;
        _ = _api.ListenEventsAsync((eventName, json) => Application.Current.Dispatcher.BeginInvoke(() => HandleEvent(eventName, json)), token);
    }

    private void HandleEvent(string eventName, string json)
    {
        try
        {
            switch (eventName)
            {
                case "ready":
                    _ = RefreshDataAsync();
                    _ = RefreshOpenChatAsync();
                    break;
                case "friend.request": NoticeMessage = "A friend request arrived."; _ = RefreshDataAsync(); break;
                case "friend.accept": case "friend.reject": case "friend.unblocked": NoticeMessage = $"Friend relationship updated: {eventName}."; _ = RefreshDataAsync(); break;
                case "friend.blocked": NoticeMessage = "A friend relationship was blocked."; _ = RefreshDataAsync(); break;
                case "device.paired": NoticeMessage = "A device paired with your account."; _ = RefreshDataAsync(); break;
                case "chat.message":
                    var envelope = JsonSerializer.Deserialize<RealtimeMessageEnvelope>(json, JsonOptions);
                    if (envelope is not null && envelope.ChatId == CurrentChatId && Messages.All(message => message.Id != envelope.Id))
                    {
                        Messages.Add(envelope.ToMessage());
                        if (envelope.SenderId != _currentUserId) _ = _api.MarkChatReadAsync(CurrentChatId, _lifetime.Token);
                    }
                    _ = RefreshDataAsync(); break;
                case "chat.read":
                    UpdateMessageStatuses(json, "Read");
                    break;
                case "chat.delivered":
                    UpdateMessageStatuses(json, "Delivered");
                    break;
                case "chat.typing":
                    var typing = JsonSerializer.Deserialize<TypingEventEnvelope>(json, JsonOptions);
                    TypingStatus = typing?.IsTyping == true ? "Typing…" : "";
                    break;
                case "transfer.request":
                    var request = JsonSerializer.Deserialize<TransferEventEnvelope>(json, JsonOptions);
                    if (request is not null) { SelectedIncomingTransfer = request.ToTransfer(); NoticeMessage = $"Incoming transfer: {request.Items.FirstOrDefault()?.FileName ?? "files"}. Review and accept or decline."; }
                    _ = RefreshDataAsync(); break;
                case "transfer.accept": case "transfer.decline": case "transfer.cancelled":
                    ResolveTransferWaiter(json, eventName[(eventName.LastIndexOf('.') + 1)..]);
                    if (eventName == "transfer.cancelled") { NoticeMessage = "A transfer was cancelled."; _ = RefreshDataAsync(); }
                    break;
                case "transfer.progress":
                    var progressEvent = JsonSerializer.Deserialize<TransferProgressEvent>(json, JsonOptions);
                    if (progressEvent is not null && progressEvent.TransferId == SelectedTransferId && progressEvent.Size > 0)
                    {
                        TransferPercent = Math.Clamp(progressEvent.ReceivedBytes * 100d / progressEvent.Size, 0, 100);
                        NoticeMessage = $"Server Relay · {TransferPercent:N0}%";
                    }
                    break;
                case "transfer.completed": ResolveTransferWaiter(json, "completed"); NoticeMessage = "Transfer completed and verified by the server."; _ = RefreshDataAsync(); break;
                case "transfer.failed": ResolveTransferWaiter(json, "failed"); NoticeMessage = "A transfer failed verification or encountered an error."; _ = RefreshDataAsync(); break;
                default: if (eventName.StartsWith("transfer.", StringComparison.Ordinal)) _ = RefreshDataAsync(); break;
            }
        }
        catch (JsonException) { NoticeMessage = "The service sent an event the client could not read."; }
    }

    private async Task RefreshOpenChatAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentChatId)) return;
        try
        {
            var result = await _api.GetMessagesAsync(CurrentChatId, _lifetime.Token);
            foreach (var message in result.Messages)
                if (Messages.All(existing => existing.Id != message.Id)) Messages.Add(message);
            await _api.MarkChatReadAsync(CurrentChatId, _lifetime.Token);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException) { }
    }

    private void UpdateMessageStatuses(string json, string status)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("chatId", out var chatId) && chatId.GetString() != CurrentChatId) return;
        HashSet<string>? ids = null;
        if (root.TryGetProperty("messageIds", out var messageIds) && messageIds.ValueKind == JsonValueKind.Array)
            ids = messageIds.EnumerateArray().Select(element => element.GetString() ?? "").ToHashSet(StringComparer.Ordinal);
        foreach (var message in Messages)
            if ((ids is null && message.SenderId == _currentUserId) || (ids?.Contains(message.Id) ?? false)) message.Status = status;
    }

    private void ResolveTransferWaiter(string json, string result)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("transferId", out var idElement)) return;
            var id = idElement.GetString();
            if (!string.IsNullOrEmpty(id) && _transferWaiters.TryGetValue(id, out var waiter)) waiter.TrySetResult(result);
        }
        catch (JsonException) { }
    }

    private async Task RequestFriendAsync()
    {
        if (string.IsNullOrWhiteSpace(FriendUsername)) return;
        IsBusy = true; ErrorMessage = "";
        try { await _api.RequestFriendAsync(FriendUsername.Trim(), _lifetime.Token); FriendUsername = ""; NoticeMessage = "Friend request sent."; await RefreshDataAsync(); }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException) { ErrorMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task SearchUsersAsync()
    {
        ErrorMessage = "";
        try
        {
            var results = await _api.SearchUsersAsync(SearchQuery.Trim(), _lifetime.Token);
            SearchResults.Clear();
            foreach (var result in results) { SearchResults.Add(result); _knownUsers[result.Id] = result; }
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException) { ErrorMessage = ex.Message; }
    }

    private async Task AddSelectedSearchUserAsync()
    {
        if (SelectedSearchUser is null) return;
        FriendUsername = SelectedSearchUser.Username;
        await RequestFriendAsync();
        SearchResults.Clear();
    }

    private async Task UnblockSelectedFriendAsync()
    {
        if (SelectedFriend is null) return;
        try
        {
            await _api.UnblockFriendAsync(SelectedFriend.User.Id, _lifetime.Token);
            NoticeMessage = $"Unblocked {SelectedFriend.User.DisplayName}.";
            await RefreshDataAsync();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException) { ErrorMessage = ex.Message; }
    }

    private async Task CreatePairingCodeAsync()
    {
        try
        {
            var result = await _api.CreatePairingCodeAsync(_lifetime.Token);
            PairingCode = result.PairingCode;
            NoticeMessage = $"Pair code expires in {result.ExpiresInSeconds / 60} minutes. Enter it on the second signed-in device.";
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException) { ErrorMessage = ex.Message; }
    }

    private async Task PairDeviceAsync()
    {
        if (PairingCode.Length != 8) { ErrorMessage = "Enter the eight-digit pairing code."; return; }
        try
        {
            var device = await _api.PairDeviceAsync(PairingCode, DeviceName, "Windows", _lifetime.Token);
            PairingCode = "";
            NoticeMessage = $"Device paired: {device.DeviceName}.";
            await RefreshDataAsync();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException) { ErrorMessage = ex.Message; }
    }

    private async Task RevokeSelectedDeviceAsync()
    {
        if (SelectedDevice is null) return;
        try
        {
            await _api.RevokeDeviceAsync(SelectedDevice.Id, _lifetime.Token);
            NoticeMessage = $"Revoked device {SelectedDevice.DeviceName}.";
            await RefreshDataAsync();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException) { ErrorMessage = ex.Message; }
    }

    private async Task RespondFriendAsync(bool accept)
    {
        if (SelectedFriend is null) return;
        IsBusy = true;
        try { await _api.RespondFriendAsync(SelectedFriend.User.Id, accept, _lifetime.Token); NoticeMessage = accept ? "Friend request accepted." : "Friend request rejected."; await RefreshDataAsync(); }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException) { ErrorMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task RemoveFriendAsync(bool block)
    {
        if (SelectedFriend is null) return;
        IsBusy = true;
        try
        {
            var peer = SelectedFriend.User;
            if (block) await _api.BlockFriendAsync(peer.Id, _lifetime.Token); else await _api.RemoveFriendAsync(peer.Id, _lifetime.Token);
            NoticeMessage = block ? $"Blocked {peer.DisplayName}." : $"Removed {peer.DisplayName}.";
            await RefreshDataAsync();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException) { ErrorMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task OpenChatAsync()
    {
        if (SelectedFriend is null || SelectedFriend.State != "Accepted") return;
        IsBusy = true;
        try
        {
            var chat = await _api.OpenChatAsync(SelectedFriend.User.Id, _lifetime.Token);
            CurrentChatId = chat.Id; _currentPeerId = SelectedFriend.User.Id; CurrentPeerName = SelectedFriend.User.DisplayName;
            var messages = await _api.GetMessagesAsync(chat.Id, _lifetime.Token);
            Messages.Clear(); foreach (var message in messages.Messages) Messages.Add(message);
            await _api.MarkChatReadAsync(chat.Id, _lifetime.Token);
            Navigate("Chat");
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException) { ErrorMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task SendMessageAsync()
    {
        var content = MessageDraft.Trim();
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(CurrentChatId)) return;
        IsSending = true;
        try { var message = await _api.SendMessageAsync(CurrentChatId, content, ct: _lifetime.Token); if (Messages.All(existing => existing.Id != message.Id)) Messages.Add(message); MessageDraft = ""; }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException) { ErrorMessage = ex.Message; }
        finally { IsSending = false; }
    }

    private async Task RegisterDeviceAsync()
    {
        if (string.IsNullOrWhiteSpace(DeviceName)) return;
        IsBusy = true;
        try
        {
            await EnsureDeviceRegisteredAsync();
            await RefreshDataAsync();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException) { ErrorMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    public void SendDroppedFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count > 0) _ = SendFilesAsync(paths, attachToChat: Page == "Chat");
    }

    private void OpenFiles()
    {
        var picker = new OpenFileDialog { Title = "Choose files to send", Multiselect = true, CheckFileExists = true };
        if (picker.ShowDialog() == true) _ = SendFilesAsync(picker.FileNames, attachToChat: false);
    }

    private void OpenChatAttachment()
    {
        if (string.IsNullOrWhiteSpace(CurrentChatId)) { ErrorMessage = "Open a conversation before attaching a file."; return; }
        var picker = new OpenFileDialog { Title = "Attach files to chat", Multiselect = true, CheckFileExists = true };
        if (picker.ShowDialog() == true) _ = SendFilesAsync(picker.FileNames, attachToChat: true);
    }

    private async Task SendFilesAsync(IReadOnlyList<string> paths, bool attachToChat)
    {
        var peerId = attachToChat ? _currentPeerId : SelectedFriend?.User.Id ?? _currentPeerId;
        var friend = _knownUsers.GetValueOrDefault(peerId);
        if (friend is null || !Friends.Any(item => item.User.Id == peerId && item.State == "Accepted"))
        {
            ErrorMessage = "Select an accepted friend before sending files.";
            Navigate("Friends"); return;
        }
        if (paths.Count == 0) return;
        IsBusy = true; ErrorMessage = ""; TransferLogs.Clear(); TransferPercent = 0; TransferSpeed = "—"; TransferEta = "—";
        try
        {
            var created = await _transferManager.PrepareAsync(peerId, paths, _lifetime.Token);
            var transfer = created.Transfer;
            SelectedTransferId = transfer.Id;
            _sourceStore.Save(transfer.Id, paths);
            PendingTransferItems.Clear(); foreach (var item in transfer.Items) PendingTransferItems.Add(item);
            Transfers.Insert(0, transfer);
            foreach (var item in transfer.Items.Where(item => item.DuplicateDetected))
                TransferLogs.Add($"Possible duplicate detected by size and SHA-256: {item.FileName}");
            TransferLogs.Add($"Transfer request {transfer.Id} · {created.Transport}");
            NoticeMessage = $"Waiting for {friend.DisplayName} to accept · actual mode: {created.Transport}.";
            Navigate("Transfer");
            if (attachToChat && CurrentChatId.Length > 0)
            {
                var summary = transfer.Items.Count == 1 ? $"Shared {transfer.Items[0].FileName}" : $"Shared {transfer.Items.Count} files";
                var attachment = await _api.SendMessageAsync(CurrentChatId, summary, transfer.Id, _lifetime.Token);
                if (Messages.All(message => message.Id != attachment.Id)) Messages.Add(attachment);
            }
            IsBusy = false;

            var decision = await WaitForTransferStateAsync(transfer, new HashSet<string>(StringComparer.Ordinal) { "Transferring", "Cancelled", "Failed" });
            if (decision.Status != "Transferring")
            {
                NoticeMessage = decision.Status == "Cancelled" ? "Receiver declined or cancelled the transfer." : $"Transfer stopped: {decision.Status}.";
                TransferLogs.Add(NoticeMessage);
                return;
            }
            await UploadAndCompleteAsync(transfer, paths);
        }
        catch (OperationCanceledException) { NoticeMessage = "Transfer wait was interrupted. Server-accepted chunks remain available for resume."; }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorMessage = ex.Message;
            TransferLogs.Add($"Transfer failed: {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    private async Task UploadAndCompleteAsync(TransferDto transfer, IReadOnlyList<string> paths)
    {
        IsBusy = true;
        TransferLogs.Add("Uploading missing chunks · transport: Server Relay");
        var progress = new Progress<TransferProgress>(value =>
        {
            TransferPercent = value.TotalBytes <= 0 ? 100 : Math.Clamp(value.TransferredBytes * 100d / value.TotalBytes, 0, 100);
            TransferSpeed = FormatSpeed(value.BytesPerSecond);
            TransferEta = value.Remaining is null ? "—" : FormatDuration(value.Remaining.Value);
            NoticeMessage = $"{value.FileName} · Server Relay · {value.TransferredBytes:N0}/{value.TotalBytes:N0} bytes";
        });
        var completed = await _transferManager.UploadAcceptedAsync(transfer, paths, progress, _lifetime.Token);
        ReplaceTransfer(completed);
        _sourceStore.Remove(transfer.Id);
        TransferPercent = 100; TransferSpeed = "—"; TransferEta = "0 sec";
        NoticeMessage = "Transfer complete · SHA-256 verified · Server Relay.";
        TransferLogs.Add(NoticeMessage);
    }

    private async Task<TransferDto> WaitForTransferStateAsync(TransferDto transfer, IReadOnlySet<string> finalStates)
    {
        var deadline = DateTimeOffset.UtcNow.AddHours(24);
        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transferWaiters[transfer.Id] = waiter;
        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (waiter.Task.IsCompleted)
                {
                    var signal = await waiter.Task;
                    if (signal is "accept" or "completed" or "decline" or "cancelled" or "failed")
                    {
                        var fromEvent = await _api.GetTransferAsync(transfer.Id, _lifetime.Token);
                        if (finalStates.Contains(fromEvent.Status)) return fromEvent;
                    }
                }
                var current = await _api.GetTransferAsync(transfer.Id, _lifetime.Token);
                if (finalStates.Contains(current.Status)) return current;
                await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token);
            }
            throw new TimeoutException("Transfer session expired while waiting for the other device.");
        }
        finally { _transferWaiters.Remove(transfer.Id); }
    }

    private void ReplaceTransfer(TransferDto transfer)
    {
        var index = Transfers.ToList().FindIndex(item => item.Id == transfer.Id);
        if (index >= 0) Transfers[index] = transfer; else Transfers.Insert(0, transfer);
    }

    private static string FormatSpeed(double bytesPerSecond) => bytesPerSecond switch
    {
        <= 0 => "—", < 1024 => $"{bytesPerSecond:N0} B/s", < 1024 * 1024 => $"{bytesPerSecond / 1024:N1} KB/s",
        _ => $"{bytesPerSecond / (1024 * 1024):N1} MB/s"
    };

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    private async Task HandleIncomingTransferAsync(bool accept)
    {
        var transfer = SelectedIncomingTransfer;
        if (transfer is null) return;
        IsBusy = true;
        try
        {
            if (!accept)
            {
                await _api.DeclineTransferAsync(transfer.Id, _lifetime.Token);
                SelectedIncomingTransfer = null;
                NoticeMessage = "Transfer declined.";
                return;
            }
            await _api.AcceptTransferAsync(transfer.Id, _lifetime.Token);
            SelectedIncomingTransfer = null;
            ReplaceTransfer(transfer); SelectedTransferId = transfer.Id;
            Navigate("Transfer");
            IsBusy = false;
            NoticeMessage = "Accepted · waiting for the sender to complete the Server Relay upload.";
            var final = await WaitForTransferStateAsync(transfer, new HashSet<string>(StringComparer.Ordinal) { "Completed", "Failed", "Cancelled" });
            if (final.Status != "Completed") throw new ApiException($"Transfer ended with status {final.Status}.", System.Net.HttpStatusCode.Conflict);
            IsBusy = true;
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "HoshinoTransfer");
            await _transferManager.DownloadAsync(final, folder, _lifetime.Token);
            await RefreshDataAsync();
            NoticeMessage = $"Received and SHA-256 verified · Server Relay · {folder}";
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            ErrorMessage = ex.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task TransferActionAsync(string action)
    {
        if (string.IsNullOrEmpty(SelectedTransferId)) return;
        IsBusy = true;
        try
        {
            var transfer = action switch
            {
                "cancel" => await _api.CancelTransferAsync(SelectedTransferId, _lifetime.Token),
                "pause" => await _api.PauseTransferAsync(SelectedTransferId, _lifetime.Token),
                _ => await _api.ResumeTransferAsync(SelectedTransferId, _lifetime.Token),
            };
            ReplaceTransfer(transfer);
            NoticeMessage = $"Transfer {action} requested · actual mode: {transfer.Transport}.";
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException) { ErrorMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task RetrySelectedTransferAsync()
    {
        var transfer = Transfers.FirstOrDefault(item => item.Id == SelectedTransferId);
        if (transfer is null) return;
        if (!_sourceStore.TryGet(transfer.Id, out var paths))
        {
            ErrorMessage = "The original source files are not available on this device. Choose them again to create a new transfer.";
            return;
        }
        IsBusy = true; ErrorMessage = "";
        try
        {
            if (transfer.Status is "Failed" or "Cancelled")
            {
                transfer = await _api.RetryTransferAsync(transfer.Id, _lifetime.Token);
                _sourceStore.Save(transfer.Id, paths);
                ReplaceTransfer(transfer);
                SelectedTransferId = transfer.Id;
                NoticeMessage = "Retry request sent. Waiting for the receiver to accept.";
                IsBusy = false;
                transfer = await WaitForTransferStateAsync(transfer, new HashSet<string>(StringComparer.Ordinal) { "Transferring", "Cancelled", "Failed" });
                if (transfer.Status != "Transferring") throw new ApiException($"Retry was not accepted (status: {transfer.Status}).", System.Net.HttpStatusCode.Conflict);
            }
            else if (transfer.Status == "Paused")
            {
                transfer = await _api.ResumeTransferAsync(transfer.Id, _lifetime.Token);
                ReplaceTransfer(transfer);
            }
            if (transfer.Status is not "Transferring") throw new ApiException($"Transfer cannot resume from {transfer.Status}.", System.Net.HttpStatusCode.Conflict);
            await UploadAndCompleteAsync(transfer, paths);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            ErrorMessage = ex.Message;
            TransferLogs.Add($"Resume/retry failed: {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    private static string MimeFor(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".txt" => "text/plain", ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
            ".zip" => "application/zip", ".json" => "application/json", ".csv" => "text/csv", ".mp4" => "video/mp4",
            ".mp3" => "audio/mpeg", _ => "application/octet-stream"
        };

    private void Navigate(string? page)
    {
        if (string.IsNullOrWhiteSpace(page)) return;
        Page = page; PageDescription = DescriptionFor(page); NoticeMessage = "";
        if (page == "Transfer" && IsAuthenticated) _ = RefreshDataAsync();
    }

    public static string DescriptionFor(string page) => page switch
    {
        "Home" => "Account, devices, and your latest activity.",
        "Transfer" => "Chunked streaming · checksum verification · honest transport labelling.",
        "Devices" => "Registered devices for this account.",
        "Friends" => "Search by username, manage requests, and open a 1-to-1 chat.",
        "Chat" => "Realtime messages over the authenticated event stream.",
        "History" => "Recent relay transfers and their verification state.",
        "Settings" => "Service endpoint, local device registration, and session controls.",
        _ => ""
    };

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(propertyName); return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        _eventLifetime?.Cancel(); _eventLifetime?.Dispose();
        _lifetime.Cancel(); _lifetime.Dispose(); _api.Dispose();
    }

    private sealed class RealtimeMessageEnvelope
    {
        public string Id { get; set; } = ""; public string ChatId { get; set; } = ""; public string SenderId { get; set; } = "";
        public string Content { get; set; } = ""; public string? TransferId { get; set; } public string Timestamp { get; set; } = ""; public string Status { get; set; } = "";
        public MessageDto ToMessage() => new() { Id = Id, SenderId = SenderId, Content = Content, TransferId = TransferId, Timestamp = Timestamp, Status = Status };
    }

    private sealed class TypingEventEnvelope
    {
        public string ChatId { get; set; } = ""; public string UserId { get; set; } = ""; public bool IsTyping { get; set; } public string? ExpiresAt { get; set; }
    }

    private sealed class TransferProgressEvent
    {
        public string TransferId { get; set; } = ""; public string ItemId { get; set; } = ""; public long ReceivedBytes { get; set; } public long Size { get; set; } public string Transport { get; set; } = "";
    }

    private sealed class TransferEventEnvelope
    {
        public string Id { get; set; } = ""; public string SenderId { get; set; } = ""; public string ReceiverId { get; set; } = "";
        public string Status { get; set; } = ""; public string Transport { get; set; } = ""; public string ExpiresAt { get; set; } = "";
        public int ChunkSize { get; set; } = 4 * 1024 * 1024;
        public List<TransferItemDto> Items { get; set; } = [];
        public TransferDto ToTransfer() => new() { Id = Id, SenderId = SenderId, ReceiverId = ReceiverId, Status = Status, Transport = Transport, ExpiresAt = ExpiresAt, ChunkSize = ChunkSize, Items = Items };
    }
}
