import Foundation
import Network

/// Minimal HTTP chunk server over NWListener so the sender can serve transfer
/// chunks directly to the receiver over the local network.
final class LanTransferServer {
    static let defaultPort: UInt16 = 52317

    private let listener: NWListener
    private let token: String
    private var items: [String: (path: String, size: Int64)] = [:]
    private let queue = DispatchQueue(label: "hoshino.lan.server")
    private(set) var port: UInt16 = 0

    var token: String { token }

    init(token: String) throws {
        self.token = token
        let parameters = NWParameters.tcp
        let listener = try NWListener(using: parameters, on: NWEndpoint.Port(rawValue: Self.defaultPort)!)
        self.listener = listener
    }

    static func generateToken() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return bytes.map { String(format: "%02x", $0) }.joined()
    }

    static func localIPv4() -> String? {
        var address: String?
        var interfacePointer: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&interfacePointer) == 0, let first = interfacePointer else { return nil }
        defer { freeifaddrs(interfacePointer) }
        var current: UnsafeMutablePointer<ifaddrs>? = first
        while let itemPointer = current {
            let item = itemPointer.pointee
            if let sockaddr = item.ifa_addr, sockaddr.pointee.sa_family == UInt8(AF_INET) {
                var hostname = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                if getnameinfo(sockaddr, socklen_t(sockaddr.pointee.sa_len), &hostname, socklen_t(hostname.count), nil, 0, NI_NUMERICHOST) == 0 {
                    let name = String(cString: hostname)
                    if name != "127.0.0.1" { address = name; break }
                }
            }
            current = itemPointer.pointee.ifa_next
        }
        return address
    }

    func publish(itemId: String, path: String, size: Int64) {
        queue.async { self.items[itemId] = (path, size) }
    }

    func start() throws {
        listener.newConnectionHandler = { [weak self] connection in
            self?.handle(connection: connection)
        }
        listener.stateUpdateHandler = { [weak self] state in
            if case .ready = state {
                self?.port = self?.listener.port?.rawValue ?? Self.defaultPort
            }
        }
        listener.start(queue: queue)
    }

    func stop() {
        listener.cancel()
    }

    private func handle(connection: NWConnection) {
        connection.start(queue: queue)
        receiveRequest(connection: connection, buffer: Data())
    }

    private func receiveRequest(connection: NWConnection, buffer: Data) {
        guard !buffer.contains(Data("\r\n\r\n".utf8)) else {
            route(connection: connection, head: buffer)
            return
        }
        connection.receive(minimumIncompleteLength: 1, maximumLength: 8192) { [weak self] data, _, isComplete, error in
            guard let self, error == nil else { connection.cancel(); return }
            var accumulated = buffer
            if let data { accumulated.append(data) }
            if accumulated.contains(Data("\r\n\r\n".utf8)) || (isComplete && accumulated.isEmpty) {
                self.route(connection: connection, head: accumulated)
            } else if isComplete {
                connection.cancel()
            } else {
                self.receiveRequest(connection: connection, buffer: accumulated)
            }
        }
    }

    private func route(connection: NWConnection, head: Data) {
        let headerText = String(decoding: head.prefix(while: { $0 != 0 }), as: UTF8.self)
        let requestLine = headerText.components(separatedBy: "\r\n").first ?? ""
        let parts = requestLine.components(separatedBy: " ")
        guard parts.count >= 2, parts[0] == "GET", let url = URL(string: parts[1].replacingOccurrences(of: " ", with: "%20")),
              let components = URLComponents(url: url, resolvingAgainstBaseURL: false) else {
            respond(connection: connection, status: "400 Bad Request", body: Data())
            return
        }
        guard components.queryItems?.first(where: { $0.name == "token" })?.value == token else {
            respond(connection: connection, status: "403 Forbidden", body: Data("invalid capability token".utf8))
            return
        }
        let segments = components.path.split(separator: "/").map(String.init)
        guard segments.count == 4, segments[0].lowercased() == "hoshino",
              let chunkIndex = Int(segments[3]), let item = items[segments[2]] else {
            respond(connection: connection, status: "404 Not Found", body: Data("unknown transfer item".utf8))
            return
        }
        let chunkSize = 4 * 1024 * 1024
        let offset = Int64(chunkIndex) * Int64(chunkSize)
        guard offset < item.size else {
            respond(connection: connection, status: "404 Not Found", body: Data("chunk out of range".utf8))
            return
        }
        let length = Int(min(Int64(chunkSize), item.size - offset))
        guard let handle = try? FileHandle(forReadingFrom: URL(fileURLWithPath: item.path)) else {
            respond(connection: connection, status: "500 Internal Server Error", body: Data())
            return
        }
        defer { try? handle.close() }
        try? handle.seek(toOffset: UInt64(offset))
        guard let payload = try? handle.read(upToCount: length), payload.count == length else {
            respond(connection: connection, status: "500 Internal Server Error", body: Data())
            return
        }
        respond(connection: connection, status: "200 OK", body: payload)
    }

    private func respond(connection: NWConnection, status: String, body: Data) {
        let header = "HTTP/1.1 \(status)\r\nConnection: close\r\nCache-Control: no-store\r\nContent-Type: application/octet-stream\r\nContent-Length: \(body.count)\r\n\r\n"
        var payload = Data(header.utf8)
        payload.append(body)
        connection.send(content: payload, completion: .contentProcessed { [weak connection] _ in
            connection?.cancel()
        })
    }
}
