import CryptoKit
import Foundation
import SwiftUI

@main
struct HoshinoTransferApp: App {
    @StateObject private var session = SessionStore()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(session)
                .preferredColorScheme(.dark)
                .task { await session.bootstrap() }
        }
    }
}

@MainActor
final class SessionStore: ObservableObject {
    @Published private(set) var user: UserProfile?
    @Published private(set) var friends: [FriendRecord] = []
    @Published private(set) var searchResults: [UserProfile] = []
    @Published private(set) var devices: [DeviceRecord] = []
    @Published private(set) var chats: [ChatRecord] = []
    @Published private(set) var messages: [ChatMessage] = []
    @Published private(set) var transfers: [TransferRecord] = []
    @Published var incomingTransfer: TransferRecord?
    @Published private(set) var activeChat: ChatRecord?
    @Published private(set) var pairingCode: PairingCode?
    @Published var serviceStatus = "Not checked"
    @Published private(set) var connectionStatus = "Disconnected"
    @Published var errorMessage: String?
    @Published private(set) var transferFraction = 0.0
    @Published private(set) var transferSpeed = 0.0
    @Published private(set) var transferEta: TimeInterval?
    @Published private(set) var savedFiles: [URL] = []
    @Published private(set) var isWorking = false

    private let api = APIClient()
    private var eventsTask: Task<Void, Never>?
    private var typingTask: Task<Void, Never>?
    private var knownFileURLs: [String: [URL]] = [:]
    private var lastProgressRefresh: Date?

    var isAuthenticated: Bool { user != nil && api.isAuthenticated }
    var serviceHost: String { api.baseURL.host ?? "HoshinoTransfer service" }
    var currentUserId: String { user?.id ?? "" }

    var activeTransferLabel: String {
        if let transfer = transfers.first(where: { $0.status == "Transferring" }) {
            return "\(transfer.transport) · \(transfer.items.count) file\(transfer.items.count == 1 ? "" : "s")"
        }
        return isWorking ? "Working…" : "No active transfer"
    }

    var activeTransferDetail: String {
        let percent = Int((min(1, max(0, transferFraction)) * 100).rounded())
        if transferSpeed > 0 {
            let speed = ByteCountFormatter.string(fromByteCount: Int64(transferSpeed), countStyle: .file)
            if let eta = transferEta, eta.isFinite, eta > 0 {
                return "\(percent)% · \(speed)/s · \(Int(eta.rounded()))s left"
            }
            return "\(percent)% · \(speed)/s"
        }
        return "\(percent)%"
    }

    func bootstrap() async {
        do {
            if let restored = try await api.restoreProfile() {
                user = restored
                try await ensureDeviceRegistered()
                await refreshAll()
                startEventStream()
            }
        } catch {
            errorMessage = error.localizedDescription
        }
        do { serviceStatus = try await checkHealth() }
        catch { serviceStatus = error.localizedDescription }
    }

    func authenticate(username: String, displayName: String, password: String, registering: Bool) async throws {
        isWorking = true
        defer { isWorking = false }
        let result = try await api.authenticate(username: username, displayName: displayName, password: password, registering: registering)
        user = result.user
        errorMessage = nil
        try await ensureDeviceRegistered()
        startEventStream()
        await refreshAll()
    }

    func signOut() async {
        eventsTask?.cancel(); eventsTask = nil
        typingTask?.cancel(); typingTask = nil
        do { try await api.logout() } catch { errorMessage = error.localizedDescription }
        user = nil; friends = []; searchResults = []; devices = []; chats = []; messages = []; transfers = []
        incomingTransfer = nil; activeChat = nil; connectionStatus = "Disconnected"
    }

    func checkHealth() async throws -> String {
        let result = try await api.health()
        return "Connected · API v\(result.apiVersion)"
    }

    func refreshAll() async {
        guard isAuthenticated else { return }
        do {
            async let friendsValue = api.friends()
            async let devicesValue = api.devices()
            async let chatsValue = api.chats()
            async let transfersValue = api.transfers()
            friends = try await friendsValue
            devices = try await devicesValue
            chats = try await chatsValue
            transfers = try await transfersValue
            if incomingTransfer == nil || incomingTransfer?.status != "Pending" {
                incomingTransfer = transfers.first(where: { $0.receiverId == currentUserId && $0.status == "Pending" })
            }
            serviceStatus = "Connected · \(serviceHost)"
            errorMessage = nil
        } catch { errorMessage = error.localizedDescription }
    }

    func searchUsers(_ query: String) async {
        guard query.trimmingCharacters(in: .whitespacesAndNewlines).count >= 3 else { searchResults = []; return }
        do { searchResults = try await api.searchUsers(query.trimmingCharacters(in: .whitespacesAndNewlines)) }
        catch { errorMessage = error.localizedDescription }
    }

    func addFriend(_ user: UserProfile) async {
        do { try await api.requestFriend(username: user.username); await refreshAll() }
        catch { errorMessage = error.localizedDescription }
    }

    func answerFriendRequest(_ friend: FriendRecord, accept: Bool) async {
        do { try await api.respondFriend(userId: friend.user.id, accept: accept); await refreshAll() }
        catch { errorMessage = error.localizedDescription }
    }

    func removeFriend(_ friend: FriendRecord) async {
        do { try await api.removeFriend(friend.user.id); await refreshAll() }
        catch { errorMessage = error.localizedDescription }
    }

    func setBlocked(_ friend: FriendRecord, blocked: Bool) async {
        do { try await api.setBlocked(friend.user.id, blocked: blocked); await refreshAll() }
        catch { errorMessage = error.localizedDescription }
    }

    func registerCurrentDevice(name: String) async {
        isWorking = true
        errorMessage = nil
        defer { isWorking = false }
        do { _ = try await api.registerDevice(name: name, platform: "iOS"); await refreshAll(); startEventStream() }
        catch { errorMessage = error.localizedDescription }
    }

    func createPairingCode() async {
        isWorking = true
        errorMessage = nil
        defer { isWorking = false }
        do { pairingCode = try await api.createPairingCode() }
        catch { errorMessage = error.localizedDescription }
    }

    func clearPairingCode() { pairingCode = nil }

    func pairDevice(code: String, name: String) async {
        let digits = code.filter(\.isNumber)
        guard digits.count == 8 else { errorMessage = "The pairing code must be eight digits."; return }
        isWorking = true
        errorMessage = nil
        defer { isWorking = false }
        do { _ = try await api.pairDevice(code: digits, name: name, platform: "iOS"); pairingCode = nil; await refreshAll() }
        catch { errorMessage = error.localizedDescription }
    }

    func revokeDevice(_ device: DeviceRecord) async {
        do { try await api.revokeDevice(device.id); await refreshAll() }
        catch { errorMessage = error.localizedDescription }
    }

    func openChat(with friend: FriendRecord) async {
        guard friend.state == "Accepted" else { errorMessage = "Accept the friend request before opening a chat."; return }
        do {
            let chat = try await api.openChat(userId: friend.user.id)
            activeChat = chat
            messages = try await api.messages(chatId: chat.id)
            try await api.markRead(chatId: chat.id)
        } catch { errorMessage = error.localizedDescription }
    }

    /// Opening a chat from the conversation list must make it the active chat, otherwise
    /// sendMessage/typing silently no-op because they are scoped to `activeChat`.
    func selectChat(_ chat: ChatRecord) async {
        activeChat = chat
        await refreshChatIfOpen()
    }

    func sendMessage(_ text: String, attachmentId: String? = nil) async {
        guard let chat = activeChat else { return }
        do {
            let message = try await api.sendMessage(chatId: chat.id, content: text, transferId: attachmentId)
            if !messages.contains(where: { $0.id == message.id }) { messages.append(message) }
        } catch { errorMessage = error.localizedDescription }
    }

    func typingChanged(_ isTyping: Bool) {
        guard let chat = activeChat else { return }
        typingTask?.cancel()
        typingTask = Task {
            if isTyping {
                try? await api.sendTyping(chatId: chat.id, isTyping: true)
                try? await Task.sleep(for: .seconds(2))
            }
            try? await api.sendTyping(chatId: chat.id, isTyping: false)
        }
    }

    func sendFiles(_ urls: [URL], to receiverId: String, in chat: ChatRecord? = nil) async {
        guard !urls.isEmpty else { return }
        if let chat { activeChat = chat }
        guard !receiverId.isEmpty else { errorMessage = "Choose a contact before sending files."; return }
        isWorking = true
        errorMessage = nil
        transferFraction = 0; transferSpeed = 0; transferEta = nil
        let scoped = urls.filter { $0.startAccessingSecurityScopedResource() }
        defer { scoped.forEach { $0.stopAccessingSecurityScopedResource() }; isWorking = false }
        do {
            let created = try await api.createTransfer(receiverId: receiverId, files: urls)
            knownFileURLs[created.transfer.id] = urls
            transfers.insert(created.transfer, at: 0)
            if let chat {
                let title = created.transfer.items.count == 1 ? "Shared \(created.transfer.items[0].fileName)" : "Shared \(created.transfer.items.count) files"
                await sendMessage(title, attachmentId: created.transfer.id)
            }
            let accepted = try await waitForTransfer(created.transfer.id, terminal: ["Transferring", "Cancelled", "Failed"])
            guard accepted.status == "Transferring" else { throw APIClientError.transferState(accepted.status) }

            // Channel setup: LAN HTTP server (Direct Wi-Fi) + P2P candidates for hole punching.
            var lanServer: LanTransferServer?
            var p2pStop: P2PChannel.StopFlag?
            var p2pSock: Int32 = -1
            if let lanIp = LanTransferServer.localIPv4() {
                do {
                    let token = LanTransferServer.generateToken()
                    let server = try LanTransferServer(token: token)
                    try server.start()
                    for (item, url) in zip(created.transfer.items, urls) {
                        server.publish(itemId: item.id, path: url.path, size: item.size)
                    }
                    try await api.registerDirectEndpoint(transferId: created.transfer.id, host: lanIp, port: Int(server.port), token: token)
                    lanServer = server
                } catch { lanServer = nil }
            }
            do {
                let sock = P2PChannel.openSocket()
                guard sock >= 0 else { throw APIClientError.invalidResponse }
                p2pSock = sock
                guard let publicMapping = P2PChannel.stunBinding(sock: sock) else { throw APIClientError.invalidResponse }
                let p2pLanIp = LanTransferServer.localIPv4() ?? "127.0.0.1"
                let lanPort = P2PChannel.localPort(sock: sock)
                try await api.registerCandidates(transferId: created.transfer.id, host: publicMapping.host, port: publicMapping.port, lanHost: p2pLanIp, lanPort: lanPort)
                let flag = P2PChannel.StopFlag()
                p2pStop = flag
                let payloads = zip(created.transfer.items, urls).map { pair in
                    P2PChannel.ItemPayload(id: pair.0.id, fileName: pair.0.fileName, size: pair.0.size, sha256: pair.0.sha256, localPath: pair.1.path)
                }
                let stopFlag = flag
                let transferURL = api.baseURL.appending(path: "api/v1/transfers/\(created.transfer.id)")
                let accessToken = api.accessToken ?? ""
                DispatchQueue.global(qos: .utility).async {
                    guard let peer = P2PChannel.establish(sock: sock, candidatesProvider: {
                        P2PChannel.fetchPeerCandidates(transferURL: transferURL, token: accessToken)
                    }, shouldStop: { stopFlag.isSet }) else { return }
                    P2PChannel.serve(sock: sock, peer: peer, payloads: payloads, shouldStop: { stopFlag.isSet })
                }
            } catch { }

            var terminal: TransferRecord?
            let deadline = Date().addingTimeInterval(15 * 60)
            while Date() < deadline {
                try Task.checkCancellation()
                let current = try await api.transfer(created.transfer.id)
                if ["Completed", "Failed", "Cancelled"].contains(current.status) { terminal = current; break }
                if current.relayRequested == true { break }
                try await Task.sleep(for: .seconds(2))
            }
            p2pStop?.set()
            lanServer?.stop()
            if p2pSock >= 0 { P2PChannel.closeSocket(p2pSock) }

            if let directFinal = terminal, directFinal.status == "Completed" {
                replaceTransfer(directFinal)
                transferFraction = 1
            } else {
                // Relay fallback (receiver asked for it, direct unavailable, or timed out).
                let completed = try await api.uploadAcceptedTransfer(accepted, sourceURLs: urls) { [weak self] fraction, speed, eta in
                    self?.transferFraction = fraction
                    self?.transferSpeed = speed
                    self?.transferEta = eta
                }
                replaceTransfer(completed)
                transferFraction = 1
            }
        } catch { errorMessage = error.localizedDescription }
        await refreshAll()
    }

    func acceptIncoming(_ transfer: TransferRecord) async {
        isWorking = true
        defer { isWorking = false }
        do {
            _ = try await api.transferAction(transfer.id, action: "accept")
            incomingTransfer = nil
            let active = try await waitForTransfer(transfer.id, terminal: ["Transferring", "Failed", "Cancelled"])
            guard active.status == "Transferring" else { throw APIClientError.transferState(active.status) }

            // Wait briefly for the sender to announce its channels.
            var current = active
            var deadline = Date().addingTimeInterval(20)
            while current.directInfo == nil && (current.peerCandidates?.isEmpty ?? true) && Date() < deadline {
                try await Task.sleep(for: .seconds(2))
                current = try await api.transfer(transfer.id)
            }

            let folder = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appending(path: "HoshinoTransfer")
            var saved: [URL] = []
            var p2pFailed = false
            if let peers = current.peerCandidates, !peers.isEmpty {
                do {
                    saved = try await downloadP2P(transfer: current, peers: peers, to: folder)
                    _ = try await api.completeP2PTransfer(transfer.id)
                } catch {
                    p2pFailed = true
                    for url in saved { try? FileManager.default.removeItem(at: url) }
                    saved = []
                }
            }
            if saved.isEmpty, !p2pFailed || current.directInfo != nil,
               let direct = current.directInfo, let host = direct.host.isEmpty ? nil : direct.host, let port = direct.port {
                saved = try await downloadDirect(transfer: current, direct: direct, host: host, port: port, to: folder)
                _ = try await api.completeDirectTransfer(transfer.id)
            } else if saved.isEmpty {
                try await api.requestRelayFallback(transferId: transfer.id)
                let completed = try await waitForTransfer(transfer.id, terminal: ["Completed", "Failed", "Cancelled"])
                guard completed.status == "Completed" else { throw APIClientError.transferState(completed.status) }
                for item in completed.items {
                    let name = URL(fileURLWithPath: item.fileName).lastPathComponent
                    let destination = folder.appending(path: "\(UUID().uuidString.prefix(8))_\(name)")
                    try await api.download(item: item, transferId: completed.id, destination: destination)
                    saved.append(destination)
                }
            }
            savedFiles = saved
        } catch { errorMessage = error.localizedDescription }
        await refreshAll()
    }

    private func downloadP2P(transfer: TransferRecord, peers: [PeerCandidate], to folder: URL) async throws -> [URL] {
        let sock = P2PChannel.openSocket()
        guard sock >= 0 else { throw APIClientError.invalidResponse }
        defer { P2PChannel.closeSocket(sock) }
        guard let publicMapping = P2PChannel.stunBinding(sock: sock) else { throw APIClientError.invalidResponse }
        let lanIp = LanTransferServer.localIPv4() ?? "127.0.0.1"
        let lanPort = P2PChannel.localPort(sock: sock)
        try await api.registerCandidates(transferId: transfer.id, host: publicMapping.host, port: publicMapping.port, lanHost: lanIp, lanPort: lanPort)
        var candidates: [P2PChannel.Endpoint] = []
        for peer in peers {
            if let endpoint = P2PChannel.endpoint(host: peer.host, port: peer.port) { candidates.append(endpoint) }
            if let lanHost = peer.lanHost, let lanPort = peer.lanPort, let endpoint = P2PChannel.endpoint(host: lanHost, port: lanPort) { candidates.append(endpoint) }
        }
        guard !candidates.isEmpty,
              let peerEndpoint = P2PChannel.establish(sock: sock, candidatesProvider: { candidates }, shouldStop: { false }) else {
            throw APIClientError.invalidResponse
        }
        let payloads = transfer.items.map { item in
            P2PChannel.ItemPayload(id: item.id, fileName: item.fileName, size: item.size, sha256: item.sha256, localPath: nil)
        }
        return try P2PChannel.receiveAll(sock: sock, peer: peerEndpoint, items: payloads, folder: folder,
                                         shouldStop: { false }) { fraction in
            Task { @MainActor in self.transferFraction = fraction }
        }
    }

    private func downloadDirect(transfer: TransferRecord, direct: DirectEndpointInfo, host: String, port: Int, to folder: URL) async throws -> [URL] {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        guard let token = direct.token else { throw APIClientError.invalidResponse }
        var saved: [URL] = []
        var downloaded = 0.0
        let total = Double(max(1, transfer.items.reduce(0) { $0 + $1.size }))
        for item in transfer.items {
            let name = URL(fileURLWithPath: item.fileName).lastPathComponent
            let destination = folder.appending(path: "\(UUID().uuidString.prefix(8))_\(name)")
            FileManager.default.createFile(atPath: destination.path, contents: nil)
            let handle = try FileHandle(forWritingTo: destination)
            defer { try? handle.close() }
            var hasher = SHA256()
            let chunkSize = 4 * 1024 * 1024
            var offset = 0
            while offset < item.size {
                try Task.checkCancellation()
                let length = Int(min(Int64(chunkSize), item.size - Int64(offset)))
                let index = offset / chunkSize
                let urlString = "http://\(host):\(port)/hoshino/\(transfer.id)/\(item.id)/\(index)?token=\(token)"
                guard let url = URL(string: urlString) else { throw APIClientError.invalidResponse }
                let chunk = try await api.downloadDirectChunk(url: url)
                guard chunk.count == length else { throw APIClientError.sourceChanged(item.fileName) }
                try handle.write(contentsOf: chunk)
                hasher.update(data: chunk)
                offset += length
                downloaded += Double(chunk.count)
                transferFraction = min(1, downloaded / total)
            }
            let digest = hasher.finalize().map { String(format: "%02x", $0) }.joined()
            guard digest == item.sha256 else { throw APIClientError.hashMismatch(item.fileName) }
            saved.append(destination)
        }
        return saved
    }

    func declineIncoming(_ transfer: TransferRecord) async {
        do { _ = try await api.transferAction(transfer.id, action: "decline"); incomingTransfer = nil; await refreshAll() }
        catch { errorMessage = error.localizedDescription }
    }

    func cancelTransfer(_ transfer: TransferRecord) async {
        do { _ = try await api.transferAction(transfer.id, action: "cancel"); await refreshAll() }
        catch { errorMessage = error.localizedDescription }
    }

    func pauseTransfer(_ transfer: TransferRecord) async {
        do { _ = try await api.transferAction(transfer.id, action: "pause"); await refreshAll() }
        catch { errorMessage = error.localizedDescription }
    }

    func resumeTransfer(_ transfer: TransferRecord) async {
        guard let urls = knownFileURLs[transfer.id] else { errorMessage = "Original files must be selected again to resume this transfer."; return }
        isWorking = true
        defer { isWorking = false }
        do {
            let resumed = try await api.transferAction(transfer.id, action: "resume")
            let complete = try await api.uploadAcceptedTransfer(resumed, sourceURLs: urls) { [weak self] fraction, speed, eta in
                self?.transferFraction = fraction; self?.transferSpeed = speed; self?.transferEta = eta
            }
            replaceTransfer(complete)
            transferFraction = 1
            await refreshAll()
        } catch { errorMessage = error.localizedDescription }
    }

    func retryTransfer(_ transfer: TransferRecord) async {
        guard let urls = knownFileURLs[transfer.id] else { errorMessage = "Select the source files again to retry."; return }
        isWorking = true
        defer { isWorking = false }
        do {
            let retry = try await api.transferAction(transfer.id, action: "retry")
            knownFileURLs[retry.id] = urls
            transfers.insert(retry, at: 0)
            let accepted = try await waitForTransfer(retry.id, terminal: ["Transferring", "Cancelled", "Failed"])
            guard accepted.status == "Transferring" else { throw APIClientError.transferState(accepted.status) }
            let complete = try await api.uploadAcceptedTransfer(accepted, sourceURLs: urls) { [weak self] fraction, speed, eta in
                self?.transferFraction = fraction; self?.transferSpeed = speed; self?.transferEta = eta
            }
            replaceTransfer(complete)
            await refreshAll()
        } catch { errorMessage = error.localizedDescription }
    }

    func refreshChatIfOpen() async {
        guard let activeChat else { return }
        do { messages = try await api.messages(chatId: activeChat.id); try await api.markRead(chatId: activeChat.id) }
        catch { errorMessage = error.localizedDescription }
    }

    private func ensureDeviceRegistered() async throws {
        if api.deviceId != nil {
            do { try await api.heartbeatDevice(); return }
            catch APIClientError.server(let status, _) where status == 404 { }
        }
        _ = try await api.registerDevice(name: UIDevice.current.name, platform: "iOS")
    }

    private func startEventStream() {
        eventsTask?.cancel()
        eventsTask = Task { [weak self] in
            guard let self else { return }
            while !Task.isCancelled && self.isAuthenticated {
                do {
                    self.connectionStatus = "Connected"
                    try await self.api.listenEvents { [weak self] eventName, data in
                        guard let self else { return }
                        Task { @MainActor in
                            await self.handleEvent(eventName, data: data)
                        }
                    }
                } catch {
                    self.connectionStatus = "Reconnecting…"
                    try? await Task.sleep(for: .seconds(2))
                }
            }
            self.connectionStatus = "Disconnected"
        }
    }

    private func handleEvent(_ name: String, data: Data) async {
        switch name {
        case "ready":
            connectionStatus = "Connected"
            await refreshAll()
            await refreshChatIfOpen()
        case "chat.message":
            if let message = try? JSONDecoder().decode(ChatMessage.self, from: data), !messages.contains(where: { $0.id == message.id }) {
                if message.chatId == activeChat?.id { messages.append(message); try? await api.markRead(chatId: message.chatId ?? "") }
            }
            await refreshAll()
        case "transfer.request":
            if let transfer = try? JSONDecoder().decode(TransferRecord.self, from: data) {
                incomingTransfer = transfer
                if !transfers.contains(where: { $0.id == transfer.id }) { transfers.insert(transfer, at: 0) }
            }
        case "transfer.progress":
            // Progress fires per chunk. Refetching friends/devices/chats/transfers on every
            // event floods the SSE stream, so only the transfer list is refreshed, at most
            // once per second.
            if lastProgressRefresh == nil || Date().timeIntervalSince(lastProgressRefresh!) > 1 {
                lastProgressRefresh = Date()
                await refreshTransfersOnly()
            }
        case "transfer.completed", "transfer.failed", "transfer.cancelled":
            await refreshAll()
        default:
            if name.hasPrefix("friend.") || name.hasPrefix("device.") || name.hasPrefix("chat.") { await refreshAll() }
        }
    }

    private func refreshTransfersOnly() async {
        guard isAuthenticated else { return }
        guard let value = try? await api.transfers() else { return }
        transfers = value
        if incomingTransfer == nil || incomingTransfer?.status != "Pending" {
            incomingTransfer = value.first(where: { $0.receiverId == currentUserId && $0.status == "Pending" })
        }
    }

    private func waitForTransfer(_ id: String, terminal: Set<String>) async throws -> TransferRecord {
        let end = Date().addingTimeInterval(24 * 60 * 60)
        while Date() < end {
            try Task.checkCancellation()
            let value = try await api.transfer(id)
            if terminal.contains(value.status) { return value }
            try await Task.sleep(for: .seconds(2))
        }
        throw APIClientError.transferExpired
    }

    private func replaceTransfer(_ transfer: TransferRecord) {
        if let index = transfers.firstIndex(where: { $0.id == transfer.id }) { transfers[index] = transfer }
        else { transfers.insert(transfer, at: 0) }
    }
}
