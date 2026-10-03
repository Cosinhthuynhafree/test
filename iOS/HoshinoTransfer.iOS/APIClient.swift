import CryptoKit
import Foundation

struct APIErrorEnvelope: Decodable {
    struct Detail: Decodable { let code: String; let message: String }
    let error: Detail
}

struct UserProfile: Codable, Identifiable {
    let id: String
    let username: String
    let displayName: String
}

struct SessionTokens: Codable {
    let accessToken: String
    let refreshToken: String
    let accessExpiresAt: Int64
    let refreshExpiresAt: Int64
}

struct AuthResponse: Decodable {
    let user: UserProfile
    let session: SessionTokens
}

struct SessionResponse: Decodable { let session: SessionTokens }
struct UserResponse: Decodable { let user: UserProfile }
struct HealthResponse: Decodable { let status: String; let service: String; let apiVersion: Int }
struct AuthRequest: Encodable { let username: String; let displayName: String?; let password: String }
struct FriendsResponse: Decodable { let friends: [FriendRecord] }
struct FriendRecord: Codable, Identifiable {
    var id: String { user.id }
    let user: UserProfile
    let state: String
    let direction: String
    let online: Bool
    let createdAt: String?
    let updatedAt: String?
}
struct UsersResponse: Decodable { let users: [UserProfile] }
struct DevicesResponse: Decodable { let devices: [DeviceRecord] }
struct DeviceEnvelope: Decodable { let device: DeviceRecord }
struct DeviceRecord: Codable, Identifiable {
    let id: String
    let deviceName: String
    let platform: String
    let lastSeen: String?
    let createdAt: String?
    let online: Bool?
    let status: String?
}
struct PairingCode: Decodable { let pairingId: String; let pairingCode: String; let expiresAt: String; let expiresInSeconds: Int }
struct ChatsResponse: Decodable { let chats: [ChatRecord] }
struct ChatEnvelope: Decodable { let chat: ChatRecord }
struct ChatRecord: Codable, Identifiable {
    let id: String
    let user: UserProfile?
    let userId: String?
    let online: Bool?
    let lastMessage: String?
    let createdAt: String?
}
struct MessagesResponse: Decodable { let messages: [ChatMessage] }
struct MessageEnvelope: Decodable { let message: ChatMessage }
struct ChatMessage: Codable, Identifiable {
    let id: String
    let chatId: String?
    let senderId: String
    let receiverId: String?
    let content: String
    let transferId: String?
    let timestamp: String
    var status: String
}
struct TransfersResponse: Decodable { let transfers: [TransferRecord] }
struct TransferEnvelope: Decodable { let transfer: TransferRecord }
struct TransferCreateResponse: Decodable { let transfer: TransferRecord; let chunkSize: Int; let transport: String }
struct TransferRecord: Codable, Identifiable {
    let id: String
    let senderId: String
    let receiverId: String
    var status: String
    let transport: String
    let expiresAt: String
    let chunkSize: Int?
    let items: [TransferItem]
}
struct TransferItem: Codable, Identifiable {
    let id: String
    let fileName: String
    let size: Int64
    let mimeType: String
    let sha256: String
    var status: String
    let duplicateDetected: Bool?
    var receivedBytes: Int64?
    var receivedChunks: [Int]?
    var nextChunkIndex: Int?
}
struct TransferProgressResponse: Decodable {
    let transferId: String
    let status: String
    let transport: String
    let chunkSize: Int
    let totalBytes: Int64
    let receivedBytes: Int64
    let items: [Item]
    struct Item: Decodable {
        let itemId: String
        let size: Int64
        let receivedBytes: Int64
        let receivedChunks: [Int]
        let nextChunkIndex: Int
        let chunkCount: Int
    }
}
struct TransferItemRequest: Encodable { let fileName: String; let size: Int64; let mimeType: String; let sha256: String }
struct OpenChatBody: Encodable { let userId: String }
struct SendMessageBody: Encodable { let content: String; let transferId: String? }
struct CreateTransferBody: Encodable { let receiverId: String; let items: [TransferItemRequest] }
struct RegisterDeviceBody: Encodable { let deviceName: String; let platform: String }
struct PairDeviceBody: Encodable { let pairingCode: String; let deviceName: String; let platform: String }
struct RequestFriendBody: Encodable { let username: String }
struct FriendActionBody: Encodable { let userId: String }
struct TypingBody: Encodable { let isTyping: Bool }
struct RefreshTokenBody: Encodable { let refreshToken: String }
struct EmptyResponse: Decodable {}

@MainActor
final class APIClient {
    private let session: URLSession
    private let keychain = SessionKeychain()
    private(set) var accessToken: String?
    private(set) var refreshToken: String?
    private(set) var accessExpiresAt: Int64 = 0
    private(set) var deviceId: String?
    private var refreshTask: Task<Void, Error>?

    let baseURL: URL
    var isAuthenticated: Bool { accessToken != nil && refreshToken != nil }

    init(session: URLSession = .shared) {
        self.session = session
        var endpoint = "https://rt2ucj.taild7fb6f.ts.net/"
        #if DEBUG
        if let override = ProcessInfo.processInfo.environment["HOSHINOTRANSFER_API_URL"], !override.isEmpty {
            endpoint = override
        }
        #endif
        if !endpoint.hasSuffix("/") { endpoint += "/" }
        baseURL = URL(string: endpoint)!
        restoreTokens()
    }

    func authenticate(username: String, displayName: String?, password: String, registering: Bool) async throws -> AuthResponse {
        let body = try JSONEncoder().encode(AuthRequest(username: username, displayName: registering ? displayName : nil, password: password))
        let result: AuthResponse = try await send(registering ? "api/v1/auth/register" : "api/v1/auth/login", method: "POST", body: body, authenticated: false)
        apply(result.session)
        return result
    }

    func restoreProfile() async throws -> UserProfile? {
        guard isAuthenticated else { return nil }
        if Date.now.timeIntervalSince1970 * 1000 >= Double(accessExpiresAt - 60_000) {
            do { try await refresh() }
            catch APIClientError.server { signOutLocal(); return nil }
        }
        do {
            let response: UserResponse = try await send("api/v1/users/me")
            return response.user
        } catch APIClientError.server(let status, _) where status == 401 {
            signOutLocal()
            return nil
        }
    }

    func health() async throws -> HealthResponse { try await send("api/health", authenticated: false) }

    func logout() async throws {
        defer { signOutLocal() }
        guard isAuthenticated else { return }
        try await sendEmpty("api/v1/auth/logout", method: "POST", body: Data("{}".utf8))
    }

    func registerDevice(name: String, platform: String) async throws -> DeviceRecord {
        let response: DeviceEnvelope = try await send("api/v1/devices", method: "POST", body: JSONEncoder().encode(RegisterDeviceBody(deviceName: name, platform: platform)))
        deviceId = response.device.id
        persistTokens()
        return response.device
    }

    func heartbeatDevice() async throws {
        guard let deviceId else { return }
        try await sendEmpty("api/v1/devices/\(deviceId)/heartbeat", method: "POST", body: Data("{}".utf8))
    }

    func devices() async throws -> [DeviceRecord] { try await send("api/v1/devices").devices }
    func createPairingCode() async throws -> PairingCode { try await send("api/v1/devices/pairing", method: "POST", body: Data("{}".utf8)) }

    func pairDevice(code: String, name: String, platform: String) async throws -> DeviceRecord {
        let response: DeviceEnvelope = try await send("api/v1/devices/pair", method: "POST", body: JSONEncoder().encode(PairDeviceBody(pairingCode: code, deviceName: name, platform: platform)))
        deviceId = response.device.id
        persistTokens()
        return response.device
    }

    func revokeDevice(_ id: String) async throws {
        try await sendEmpty("api/v1/devices/\(id)", method: "DELETE")
        if deviceId == id { deviceId = nil; persistTokens() }
    }

    func searchUsers(_ query: String) async throws -> [UserProfile] {
        guard var components = URLComponents(url: baseURL.appending(path: "api/v1/users/search"), resolvingAgainstBaseURL: false) else { throw APIClientError.invalidResponse }
        components.queryItems = [URLQueryItem(name: "q", value: query)]
        guard let url = components.url else { throw APIClientError.invalidResponse }
        return try await sendURL(url).users
    }

    func friends() async throws -> [FriendRecord] { try await send("api/v1/friends").friends }

    func requestFriend(username: String) async throws {
        try await sendEmpty("api/v1/friends/request", method: "POST", body: JSONEncoder().encode(RequestFriendBody(username: username)))
    }

    func respondFriend(userId: String, accept: Bool) async throws {
        try await sendEmpty("api/v1/friends/\(accept ? "accept" : "reject")", method: "POST", body: JSONEncoder().encode(FriendActionBody(userId: userId)))
    }

    func removeFriend(_ userId: String) async throws {
        try await sendEmpty("api/v1/friends/\(userId)", method: "DELETE")
    }

    func setBlocked(_ userId: String, blocked: Bool) async throws {
        try await sendEmpty(blocked ? "api/v1/friends/block" : "api/v1/friends/unblock", method: "POST", body: JSONEncoder().encode(FriendActionBody(userId: userId)))
    }

    func chats() async throws -> [ChatRecord] { try await send("api/v1/chats").chats }

    func openChat(userId: String) async throws -> ChatRecord {
        let body = JSONEncoder().encode(OpenChatBody(userId: userId))
        let envelope: ChatEnvelope = try await send("api/v1/chats", method: "POST", body: body)
        return envelope.chat
    }

    func messages(chatId: String) async throws -> [ChatMessage] { try await send("api/v1/chats/\(chatId)/messages").messages }

    func sendMessage(chatId: String, content: String, transferId: String? = nil) async throws -> ChatMessage {
        let body = JSONEncoder().encode(SendMessageBody(content: content, transferId: transferId))
        let envelope: MessageEnvelope = try await send("api/v1/chats/\(chatId)/messages", method: "POST", body: body)
        return envelope.message
    }

    func markRead(chatId: String) async throws {
        try await sendEmpty("api/v1/chats/\(chatId)/read", method: "POST", body: Data("{}".utf8))
    }

    func sendTyping(chatId: String, isTyping: Bool) async throws {
        try await sendEmpty("api/v1/chats/\(chatId)/typing", method: "POST", body: JSONEncoder().encode(TypingBody(isTyping: isTyping)))
    }

    func transfers() async throws -> [TransferRecord] { try await send("api/v1/transfers").transfers }

    func createTransfer(receiverId: String, files: [URL]) async throws -> TransferCreateResponse {
        let items = try files.map { url -> TransferItemRequest in
            let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
            let size = (attributes[.size] as? NSNumber)?.int64Value ?? 0
            return TransferItemRequest(fileName: url.lastPathComponent, size: size, mimeType: mimeType(for: url), sha256: try hashFile(at: url))
        }
        let body = JSONEncoder().encode(CreateTransferBody(receiverId: receiverId, items: items))
        return try await send("api/v1/transfers/create", method: "POST", body: body)
    }

    func transfer(_ id: String) async throws -> TransferRecord { try await send("api/v1/transfers/\(id)").transfer }
    func transferProgress(_ id: String) async throws -> TransferProgressResponse { try await send("api/v1/transfers/\(id)/progress") }

    func transferAction(_ id: String, action: String) async throws -> TransferRecord {
        try await send("api/v1/transfers/\(id)/\(action)", method: "POST", body: Data("{}".utf8)).transfer
    }

    func uploadAcceptedTransfer(_ transfer: TransferRecord, sourceURLs: [URL], onProgress: @MainActor (Double, Double, TimeInterval?) -> Void) async throws -> TransferRecord {
        guard transfer.transport == "Server Relay", transfer.items.count == sourceURLs.count else { throw APIClientError.invalidResponse }
        let chunkSize = max(64 * 1024, min(transfer.chunkSize ?? 4 * 1024 * 1024, 4 * 1024 * 1024))
        let started = Date()
        var sentBytes: Int64 = 0
        for (index, item) in transfer.items.enumerated() {
            let url = sourceURLs[index]
            let handle = try FileHandle(forReadingFrom: url)
            defer { try? handle.close() }
            while true {
                let remote = try await transferProgress(transfer.id)
                if remote.status == "Paused" { try await Task.sleep(for: .seconds(1)); continue }
                guard remote.status == "Transferring" else { throw APIClientError.transferState(remote.status) }
                guard let remoteItem = remote.items.first(where: { $0.itemId == item.id }) else { throw APIClientError.invalidResponse }
                let received = Set(remoteItem.receivedChunks)
                var didUpload = false
                for chunkIndex in 0..<remoteItem.chunkCount where !received.contains(chunkIndex) {
                    try Task.checkCancellation()
                    let offset = Int64(chunkIndex) * Int64(remote.chunkSize)
                    try handle.seek(toOffset: UInt64(offset))
                    let expected = Int(min(Int64(remote.chunkSize), item.size - offset))
                    guard let data = try handle.read(upToCount: expected), data.count == expected else { throw APIClientError.sourceChanged(url.lastPathComponent) }
                    try await uploadChunk(transferId: transfer.id, itemId: item.id, index: chunkIndex, data: data)
                    sentBytes += Int64(data.count)
                    didUpload = true
                    let total = max(1, remote.totalBytes)
                    let elapsed = max(0.001, Date().timeIntervalSince(started))
                    let speed = Double(sentBytes) / elapsed
                    let remaining = max(0, total - remote.receivedBytes - Int64(data.count))
                    await onProgress(min(1, Double(remote.receivedBytes + Int64(data.count)) / Double(total)), speed, speed > 0 ? Double(remaining) / speed : nil)
                    break
                }
                if !didUpload {
                    if remoteItem.receivedBytes >= item.size { break }
                    throw APIClientError.invalidResponse
                }
            }
        }
        return try await transferAction(transfer.id, action: "complete")
    }

    func acceptAndDownload(_ transfer: TransferRecord, to folder: URL) async throws -> [URL] {
        _ = try await transferAction(transfer.id, action: "accept")
        let deadline = Date().addingTimeInterval(24 * 60 * 60)
        var completed: TransferRecord?
        while Date() < deadline {
            try Task.checkCancellation()
            let current = try await self.transfer(transfer.id)
            if current.status == "Completed" { completed = current; break }
            if current.status == "Failed" || current.status == "Cancelled" { throw APIClientError.transferState(current.status) }
            try await Task.sleep(for: .seconds(2))
        }
        guard let complete = completed else { throw APIClientError.transferExpired }
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var saved: [URL] = []
        for item in complete.items {
            let name = URL(fileURLWithPath: item.fileName).lastPathComponent
            let destination = folder.appending(path: "\(UUID().uuidString.prefix(8))_\(name)")
            try await download(item: item, transferId: complete.id, destination: destination)
            saved.append(destination)
        }
        return saved
    }

    func download(item: TransferItem, transferId: String, destination: URL) async throws {
        let url = baseURL.appending(path: "api/v1/transfers/\(transferId)/items/\(item.id)/download")
        var request = authorizedRequest(url, method: "GET")
        let (temporary, response) = try await session.download(for: request)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else { throw APIClientError.unavailable }
        let digest = try hashFile(at: temporary)
        guard constantTimeEqual(digest, item.sha256) else { throw APIClientError.hashMismatch(item.fileName) }
        if FileManager.default.fileExists(atPath: destination.path) { try FileManager.default.removeItem(at: destination) }
        try FileManager.default.moveItem(at: temporary, to: destination)
    }

    func listenEvents(_ handler: @escaping @MainActor (String, Data) -> Void) async throws {
        try await ensureFreshToken()
        let url = baseURL.appending(path: "api/v1/events")
        let request = authorizedRequest(url, method: "GET")
        let (bytes, response) = try await session.bytes(for: request)
        guard let http = response as? HTTPURLResponse else { throw APIClientError.invalidResponse }
        if http.statusCode == 401 { try await refresh(); return }
        guard http.statusCode == 200 else { throw APIClientError.unavailable }
        var eventName = "message"
        var payload: [String] = []
        for try await line in bytes.lines {
            if line.hasPrefix("event:") { eventName = String(line.dropFirst(6)).trimmingCharacters(in: .whitespaces) }
            else if line.hasPrefix("data:") { payload.append(String(line.dropFirst(5)).trimmingCharacters(in: .whitespaces)) }
            else if line.isEmpty, !payload.isEmpty {
                let data = Data(payload.joined(separator: "\n").utf8)
                await handler(eventName, data)
                eventName = "message"
                payload.removeAll(keepingCapacity: true)
            }
        }
    }

    private func uploadChunk(transferId: String, itemId: String, index: Int, data: Data) async throws {
        let url = baseURL.appending(path: "api/v1/transfers/\(transferId)/items/\(itemId)/chunks/\(index)")
        var request = authorizedRequest(url, method: "PUT")
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        request.setValue(String(data.count), forHTTPHeaderField: "Content-Length")
        let (_, response) = try await session.upload(for: request, from: data)
        guard let http = response as? HTTPURLResponse else { throw APIClientError.invalidResponse }
        if http.statusCode == 401 { try await refresh(); throw APIClientError.unauthorized }
        guard (200..<300).contains(http.statusCode) else { throw APIClientError.server(http.statusCode, "Chunk upload failed.") }
    }

    private func sendURL<Response: Decodable>(_ url: URL) async throws -> Response {
        try await ensureFreshToken()
        var request = authorizedRequest(url, method: "GET")
        var (data, response) = try await session.data(for: request)
        if (response as? HTTPURLResponse)?.statusCode == 401 {
            try await refresh()
            request = authorizedRequest(url, method: "GET")
            (data, response) = try await session.data(for: request)
        }
        return try decode(data, response: response)
    }

    private func send<Response: Decodable>(_ route: String, method: String = "GET", body: Data? = nil, authenticated: Bool = true) async throws -> Response {
        let data = try await sendRaw(route, method: method, body: body, authenticated: authenticated)
        return try decode(data, route: route)
    }

    private func sendEmpty(_ route: String, method: String = "GET", body: Data? = nil, authenticated: Bool = true) async throws {
        _ = try await sendRaw(route, method: method, body: body, authenticated: authenticated)
    }

    private func sendRaw(_ route: String, method: String, body: Data?, authenticated: Bool) async throws -> Data {
        if authenticated { try await ensureFreshToken() }
        let url = baseURL.appending(path: route)
        var request = authorizedRequest(url, method: method)
        request.httpBody = body
        if body != nil { request.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        var (data, response) = try await session.data(for: request)
        if authenticated, (response as? HTTPURLResponse)?.statusCode == 401, route != "api/v1/auth/refresh" {
            try await refresh()
            request = authorizedRequest(url, method: method)
            request.httpBody = body
            if body != nil { request.setValue("application/json", forHTTPHeaderField: "Content-Type") }
            (data, response) = try await session.data(for: request)
        }
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            let reason = (try? JSONDecoder().decode(APIErrorEnvelope.self, from: data).error.message) ?? "The service rejected the request."
            throw APIClientError.server(status, reason)
        }
        return data
    }

    private func decode<Response: Decodable>(_ data: Data, route: String) throws -> Response {
        return try JSONDecoder().decode(Response.self, from: data)
    }

    private func authorizedRequest(_ url: URL, method: String) -> URLRequest {
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.timeoutInterval = 60
        if let accessToken { request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization") }
        if let deviceId { request.setValue(deviceId, forHTTPHeaderField: "X-Device-Id") }
        return request
    }

    private func ensureFreshToken() async throws {
        guard isAuthenticated else { throw APIClientError.unauthorized }
        if Date.now.timeIntervalSince1970 * 1000 >= Double(accessExpiresAt - 60_000) { try await refresh() }
    }

    private func refresh() async throws {
        if let refreshTask { try await refreshTask.value; return }
        let task = Task { [weak self] in
            guard let self, let refreshToken = self.refreshToken else { throw APIClientError.unauthorized }
            let body = try JSONEncoder().encode(RefreshTokenBody(refreshToken: refreshToken))
            let response: SessionResponse = try await self.send("api/v1/auth/refresh", method: "POST", body: body, authenticated: false)
            await MainActor.run { self.apply(response.session) }
        }
        refreshTask = task
        defer { refreshTask = nil }
        try await task.value
    }

    private func apply(_ session: SessionTokens) {
        accessToken = session.accessToken
        refreshToken = session.refreshToken
        accessExpiresAt = session.accessExpiresAt
        persistTokens()
    }

    private func restoreTokens() {
        guard let saved = keychain.load() else { return }
        accessToken = saved.accessToken
        refreshToken = saved.refreshToken
        accessExpiresAt = saved.accessExpiresAt
        deviceId = saved.deviceId
    }

    private func persistTokens() {
        guard let accessToken, let refreshToken else { return }
        keychain.save(StoredSession(accessToken: accessToken, refreshToken: refreshToken, accessExpiresAt: accessExpiresAt, deviceId: deviceId))
    }

    private func signOutLocal() {
        accessToken = nil
        refreshToken = nil
        accessExpiresAt = 0
        deviceId = nil
        keychain.delete()
    }

    private func hashFile(at url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while true {
            let data = try handle.read(upToCount: 1024 * 1024) ?? Data()
            if data.isEmpty { break }
            hasher.update(data: data)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    private func constantTimeEqual(_ left: String, _ right: String) -> Bool {
        let a = Array(left.utf8); let b = Array(right.utf8)
        guard a.count == b.count else { return false }
        var difference: UInt8 = 0
        for index in a.indices { difference |= a[index] ^ b[index] }
        return difference == 0
    }

    private func mimeType(for url: URL) -> String {
        switch url.pathExtension.lowercased() {
        case "txt": return "text/plain"
        case "pdf": return "application/pdf"
        case "png": return "image/png"
        case "jpg", "jpeg": return "image/jpeg"
        case "zip": return "application/zip"
        case "csv": return "text/csv"
        case "json": return "application/json"
        case "mp4": return "video/mp4"
        case "mp3": return "audio/mpeg"
        default: return "application/octet-stream"
        }
    }
}

struct StoredSession: Codable { let accessToken: String; let refreshToken: String; let accessExpiresAt: Int64; let deviceId: String? }

enum APIClientError: LocalizedError {
    case invalidResponse
    case unavailable
    case unauthorized
    case transferExpired
    case hashMismatch(String)
    case sourceChanged(String)
    case transferState(String)
    case server(Int, String)

    var errorDescription: String? {
        switch self {
        case .invalidResponse: return "The service returned an invalid response."
        case .unavailable: return "The service is unavailable. Try again when you have a connection."
        case .unauthorized: return "Your session expired. Sign in again."
        case .transferExpired: return "The transfer request expired before it completed."
        case .hashMismatch(let name): return "SHA-256 verification failed for \(name)."
        case .sourceChanged(let name): return "\(name) changed or was truncated while sending."
        case .transferState(let status): return "Transfer stopped with status \(status)."
        case .server(_, let message): return message
        }
    }
}
