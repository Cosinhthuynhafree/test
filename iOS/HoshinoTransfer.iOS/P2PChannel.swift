import CryptoKit
import Foundation

/// Cross-network peer-to-peer file channel over one BSD UDP socket.
/// STUN (RFC 5389) learns the public mapping; hole punching opens the path;
/// the receiver drives reliable segment requests (60 KB, retry on loss).
enum P2PChannel {
    static let segmentSize = 60000
    private static let ping: UInt8 = 0x00, pong: UInt8 = 0x01, req: UInt8 = 0x02, data: UInt8 = 0x03

    struct Endpoint: Equatable {
        var ip: UInt32
        var port: UInt16
    }

    struct ItemPayload {
        var id: String
        var fileName: String
        var size: Int64
        var sha256: String
        var localPath: String?
    }

    // MARK: - Socket helpers

    static func openSocket() -> Int32 {
        let sock = socket(AF_INET, SOCK_DGRAM, 0)
        guard sock >= 0 else { return -1 }
        var value: Int32 = 1
        setsockopt(sock, SOL_SOCKET, SO_REUSEADDR, &value, socklen_t(MemoryLayout<Int32>.size))
        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = 0
        addr.sin_addr = in_addr(s_addr: INADDR_ANY)
        let bound = withUnsafePointer(to: &addr) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPointer in
                bind(sock, sockaddrPointer, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bound == 0 else { close(sock); return -1 }
        var timeout = timeval(tv_sec: 0, tv_usec: 200_000)
        setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        return sock
    }

    static func localPort(sock: Int32) -> UInt16 {
        var addr = sockaddr_in()
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        guard getsockname(sock, sockaddr_cast(&addr), &length) == 0 else { return 0 }
        return UInt16(bigEndian: addr.sin_port)
    }

    private static func sockaddr_cast(_ pointer: UnsafeMutablePointer<sockaddr_in>) -> UnsafeMutablePointer<sockaddr> {
        UnsafeMutablePointer<sockaddr>(OpaquePointer(pointer))
    }

    static func closeSocket(_ sock: Int32) {
        if sock >= 0 { close(sock) }
    }

    private static func resolve(_ host: String, _ port: UInt16) -> sockaddr_in? {
        var hints = addrinfo()
        hints.ai_family = AF_INET
        hints.ai_socktype = SOCK_DGRAM
        var info: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(host, String(port), &hints, &info) == 0, let first = info else { return nil }
        defer { freeaddrinfo(info) }
        return first.pointee.ai_addr.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee }
    }

    private static func sendtoPeer(_ sock: Int32, _ payload: [UInt8], _ endpoint: Endpoint) {
        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = endpoint.port.bigEndian
        addr.sin_addr = in_addr(s_addr: endpoint.ip)
        let sent = withUnsafePointer(to: &addr) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPointer in
                payload.withUnsafeBytes { raw in
                    sendto(sock, raw.baseAddress, payload.count, 0, sockaddrPointer, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        }
        _ = sent
    }

    /// Blocking receive with the socket receive timeout. Returns nil on timeout.
    private static func receiveFrom(_ sock: Int32) -> (data: [UInt8], from: Endpoint)? {
        var buffer = [UInt8](repeating: 0, count: 65536)
        var from = sockaddr_in()
        var fromLength = socklen_t(MemoryLayout<sockaddr_in>.size)
        let received = withUnsafeMutablePointer(to: &buffer) { bufferPointer in
            withUnsafeMutablePointer(to: &from) { fromPointer in
                recvfrom(sock, bufferPointer, buffer.count, 0, sockaddr_cast(fromPointer), &fromLength)
            }
        }
        guard received > 0 else { return nil }
        let data = Array(buffer[0..<received])
        var endpoint = Endpoint(ip: from.sin_addr.s_addr, port: UInt16(bigEndian: from.sin_port))
        endpoint.port = from.sin_port.bigEndian
        return (data, endpoint)
    }

    // MARK: - STUN

    /// Binds through the given socket and returns the public UDP mapping (RFC 5389 XOR-MAPPED-ADDRESS).
    static func stunBinding(sock: Int32) -> Endpoint? {
        let servers: [(String, UInt16)] = [("stun.l.google.com", 19302), ("stun.cloudflare.com", 3478)]
        for (host, port) in servers {
            guard var target = resolve(host, port) else { continue }
            var transactionId = [UInt8](repeating: 0, count: 12)
            let status = SecRandomCopyBytes(kSecRandomDefault, transactionId.count, &transactionId)
            guard status == errSecSuccess else { continue }
            var request: [UInt8] = [0x00, 0x01, 0x00, 0x00, 0x21, 0x12, 0xA4, 0x42]
            request.append(contentsOf: transactionId)
            var serverEndpoint = Endpoint(ip: target.sin_addr.s_addr, port: 0)
            serverEndpoint.port = target.sin_port.bigEndian
            for _ in 0..<2 {
                sendtoPeer(sock, request, serverEndpoint)
                let deadline = Date().addingTimeInterval(3)
                while Date() < deadline {
                    guard let response = receiveFrom(sock) else { break }
                    if let mapped = parseXorMapped(response.data, transactionId: transactionId) { return mapped }
                }
            }
        }
        return nil
    }

    private static func parseXorMapped(_ packet: [UInt8], transactionId: [UInt8]) -> Endpoint? {
        guard packet.count >= 20 else { return nil }
        var index = 20
        while index + 4 <= packet.count {
            let type = (UInt16(packet[index]) << 8) | UInt16(packet[index + 1])
            let length = (Int(packet[index + 2]) << 8) | Int(packet[index + 3])
            if type == 0x0020, length >= 8, index + 4 + length <= packet.count {
                let maskedPort = (UInt16(packet[index + 6]) << 8) | UInt16(packet[index + 7])
                let port = maskedPort ^ 0x2112
                var ip: UInt32 = 0
                for offset in 0..<4 {
                    let magicByte = UInt8((0x2112A442 >> (24 - offset * 8)) & 0xFF)
                    let byte = packet[index + 8 + offset] ^ magicByte
                    ip = (ip << 8) | UInt32(byte)
                }
                return Endpoint(ip: ip, port: port)
            }
            index += 4 + length
        }
        return nil
    }

    // MARK: - Punch + transfer

    /// Both sides spam PINGs at each other's candidates until a datagram lands.
    static func establish(sock: Int32, candidatesProvider: @escaping () -> [Endpoint],
                          shouldStop: @escaping () -> Bool) -> Endpoint? {
        let pingPayload = (0..<8).map { _ in UInt8.random(in: 0...255) }
        let deadline = Date().addingTimeInterval(25)
        var lastSend = Date.distantPast
        while Date() < deadline {
            if shouldStop() { return nil }
            if Date().timeIntervalSince(lastSend) >= 0.25 {
                lastSend = Date()
                for candidate in candidatesProvider() {
                    var packet: [UInt8] = [ping]
                    packet.append(contentsOf: pingPayload)
                    sendtoPeer(sock, packet, candidate)
                }
            }
            if let received = receiveFrom(sock) {
                if received.data.first == ping {
                    var pong: [UInt8] = [pong]
                    pong.append(contentsOf: received.data.dropFirst().prefix(8))
                    sendtoPeer(sock, pong, received.from)
                }
                if [ping, pong, req, data].contains(received.data.first) { return received.from }
            }
        }
        return nil
    }

    static func punch(sock: Int32, candidates: [Endpoint]) -> Endpoint? {
        establish(sock: sock, candidatesProvider: { candidates }, shouldStop: { false })
    }

    static func endpoint(host: String, port: Int?) -> Endpoint? {
        guard let port, port > 0, port < 65536, var addr = resolve(host, UInt16(port)) else { return nil }
        var endpoint = Endpoint(ip: addr.sin_addr.s_addr, port: 0)
        endpoint.port = addr.sin_port.bigEndian
        return endpoint
    }

    static func ipv4Bytes(_ dotted: String) -> UInt32 {
        resolve(dotted, 0)?.sin_addr.s_addr ?? INADDR_ANY
    }

    struct PeerCandidatesEnvelope: Decodable {
        let peerCandidates: [WireCandidate]?
    }
    struct WireCandidate: Decodable {
        let host: String
        let port: Int?
        let lanHost: String?
        let lanPort: Int?
    }

    /// Synchronous candidate refresh for punch threads (no MainActor involvement).
    static func fetchPeerCandidates(transferURL: URL, token: String) -> [Endpoint] {
        var request = URLRequest(url: transferURL)
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.timeoutInterval = 5
        var result: [Endpoint] = []
        let semaphore = DispatchSemaphore(value: 0)
        URLSession.shared.dataTask(with: request) { data, _, _ in
            defer { semaphore.signal() }
            guard let data,
                  let envelope = try? JSONDecoder().decode(PeerCandidatesEnvelope.self, from: data),
                  let peers = envelope.peerCandidates else { return }
            for peer in peers {
                if let endpoint = endpoint(host: peer.host, port: peer.port) { result.append(endpoint) }
                if let lanHost = peer.lanHost, let lanPort = peer.lanPort,
                   let endpoint = endpoint(host: lanHost, port: lanPort) { result.append(endpoint) }
            }
        }.resume()
        _ = semaphore.wait(timeout: .now() + 6)
        return result
    }

    /// Sender side: answer segment requests with file slices.
    static func serve(sock: Int32, peer: Endpoint, payloads: [ItemPayload], cancel: @escaping () -> Bool) {
        while !cancel() {
            guard let received = receiveFrom(sock), received.from == peer, received.data.count >= 9,
                  received.data.first == req else { continue }
            let itemIndex = readInt(received.data, 1)
            let segmentIndex = readInt(received.data, 5)
            guard itemIndex >= 0, itemIndex < payloads.count else { continue }
            let item = payloads[itemIndex]
            let offset = Int64(segmentIndex) * Int64(segmentSize)
            guard offset < item.size, let path = item.localPath,
                  let handle = FileHandle(forReadingAtPath: path) else { continue }
            defer { try? handle.close() }
            try? handle.seek(toOffset: UInt64(offset))
            let wanted = Int(min(Int64(segmentSize), item.size - offset))
            guard let payload = try? handle.read(upToCount: wanted), payload.count == wanted else { continue }
            var packet: [UInt8] = [data]
            packet.append(contentsOf: writeInt(itemIndex))
            packet.append(contentsOf: writeInt(segmentIndex))
            packet.append(contentsOf: [UInt8](payload))
            sendtoPeer(sock, packet, peer)
        }
    }

    /// Receiver side: request every segment with retries, verify SHA-256 per item.
    static func receiveAll(sock: Int32, peer: Endpoint, items: [ItemPayload], folder: URL,
                           shouldStop: @escaping () -> Bool,
                           onProgress: @escaping (Double) -> Void) throws -> [URL] {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var saved: [URL] = []
        let totalBytes = Double(max(1, items.reduce(0) { $0 + $1.size }))
        var doneBytes = 0.0
        for (itemIndex, item) in items.enumerated() {
            let name = URL(fileURLWithPath: item.fileName).lastPathComponent
            let destination = folder.appending(path: "\(UUID().uuidString.prefix(8))_\(name)")
            FileManager.default.createFile(atPath: destination.path, contents: nil)
            guard let handle = FileHandle(forWritingTo: destination) else { throw APIClientError.invalidResponse }
            defer { try? handle.close() }
            var hasher = SHA256()
            let segmentCount = Int(ceil(Double(item.size) / Double(segmentSize)))
            var received = [Bool](repeating: false, count: segmentCount)
            var missing = Set(0..<segmentCount)
            var lastSent = [Int: Date]()
            while !missing.isEmpty {
                if shouldStop() { throw CancellationError() }
                let now = Date()
                for segment in missing.prefix(24) where now.timeIntervalSince(lastSent[segment] ?? .distantPast) > 0.8 {
                    var request: [UInt8] = [req]
                    request.append(contentsOf: writeInt(itemIndex))
                    request.append(contentsOf: writeInt(segment))
                    sendtoPeer(sock, request, peer)
                    lastSent[segment] = now
                }
                guard let receivedPacket = receiveFrom(sock), receivedPacket.from == peer,
                      receivedPacket.data.count >= 9, receivedPacket.data.first == data else { continue }
                let dataItem = readInt(receivedPacket.data, 1)
                let dataSegment = readInt(receivedPacket.data, 5)
                guard dataItem == itemIndex, dataSegment < segmentCount, !received[dataSegment] else { continue }
                received[dataSegment] = true
                let payload = Array(receivedPacket.data.dropFirst(9))
                try handle.write(contentsOf: Data(payload))
                hasher.update(data: payload)
                missing.remove(dataSegment)
                lastSent.removeValue(forKey: dataSegment)
                doneBytes += Double(payload.count)
                onProgress(min(1, doneBytes / totalBytes))
            }
            let digest = hasher.finalize().map { String(format: "%02x", $0) }.joined()
            guard digest == item.sha256 else {
                try? FileManager.default.removeItem(at: destination)
                throw APIClientError.hashMismatch(item.fileName)
            }
            saved.append(destination)
        }
        return saved
    }

    final class StopFlag {
        private let lock = NSLock()
        private var value = false
        var isSet: Bool { lock.lock(); defer { lock.unlock() }; return value }
        func set() { lock.lock(); defer { lock.unlock() }; value = true }
    }

    private static func writeInt(_ value: Int) -> [UInt8] {
        let v = UInt32(value)
        return [UInt8(v >> 24 & 0xFF), UInt8(v >> 16 & 0xFF), UInt8(v >> 8 & 0xFF), UInt8(v & 0xFF)]
    }

    private static func readInt(_ buffer: [UInt8], _ offset: Int) -> Int {
        (Int(buffer[offset]) << 24) | (Int(buffer[offset + 1]) << 16) | (Int(buffer[offset + 2]) << 8) | Int(buffer[offset + 3])
    }
}
