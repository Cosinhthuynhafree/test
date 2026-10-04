import CoreImage.CIFilterBuiltins
import SwiftUI
import UniformTypeIdentifiers
import UIKit

enum PairingQR {
    /// Renders the pairing API link as a QR bitmap for the other device's camera or photo.
    static func image(for link: String, size: CGFloat = 220) -> UIImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(link.utf8)
        filter.correctionLevel = "M"
        guard let output = filter.outputImage else { return nil }
        let scaled = output.transformed(by: CGAffineTransform(scaleX: size / output.extent.width, y: size / output.extent.height))
        let context = CIContext(options: [.useSoftwareRenderer: false])
        guard let cgImage = context.createCGImage(scaled, from: scaled.extent) else { return nil }
        return UIImage(cgImage: cgImage)
    }
}

struct PairingCodeCard: View {
    let code: PairingCode
    let link: String

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Pairing code").font(.subheadline.weight(.semibold))
            Text(code.pairingCode)
                .font(.title2.monospaced().weight(.bold))
                .textSelection(.enabled)
            if let image = PairingQR.image(for: link) {
                Image(uiImage: image)
                    .interpolation(.none)
                    .resizable()
                    .frame(width: 180, height: 180)
                    .padding(10)
                    .background(Color.white)
                    .clipShape(RoundedRectangle(cornerRadius: 12))
            }
            Text(link)
                .font(.caption2).foregroundStyle(.secondary)
                .textSelection(.enabled)
            Text("Scan the QR, or import a photo of it on the other device. Expires in \(max(1, code.expiresInSeconds / 60)) minutes.")
                .font(.footnote).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.vertical, 4)
    }
}

struct RootView: View {
    @EnvironmentObject private var session: SessionStore
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        Group {
            if session.isAuthenticated {
                HomeView()
            } else {
                AuthenticationView()
            }
        }
        .tint(Color(red: 0.67, green: 0.61, blue: 1.0))
        .sheet(item: $session.incomingTransfer) { transfer in
            TransferRequestSheet(transfer: transfer)
                .presentationDetents([.medium, .large])
        }
        .onChange(of: scenePhase) { phase in
            if phase == .active {
                Task { await session.sceneBecameActive() }
            }
        }
    }
}

struct AuthenticationView: View {
    @EnvironmentObject private var session: SessionStore
    @State private var username = ""
    @State private var displayName = ""
    @State private var password = ""
    @State private var registering = false

    var body: some View {
        NavigationStack {
            VStack(spacing: 22) {
                Spacer(minLength: 28)
                Image(systemName: "arrow.up.right.and.arrow.down.left")
                    .font(.system(size: 28, weight: .semibold))
                    .foregroundStyle(.white)
                    .frame(width: 62, height: 62)
                    .background(Color.purple.opacity(0.65), in: RoundedRectangle(cornerRadius: 20))
                VStack(spacing: 7) {
                    Text("HoshinoTransfer").font(.largeTitle.weight(.bold))
                    Text("Your files, between your devices.")
                        .foregroundStyle(.secondary)
                }
                VStack(spacing: 13) {
                    if registering {
                        TextField("Display name", text: $displayName)
                            .textContentType(.name)
                            .textInputAutocapitalization(.words)
                            .autocorrectionDisabled()
                            .textFieldStyle(.roundedBorder)
                    }
                    TextField("Username", text: $username)
                        .textContentType(.username)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .textFieldStyle(.roundedBorder)
                    SecureField("Password", text: $password)
                        .textContentType(registering ? .newPassword : .password)
                        .textFieldStyle(.roundedBorder)
                    if let message = session.errorMessage {
                        Text(message).font(.footnote).foregroundStyle(.red).frame(maxWidth: .infinity, alignment: .leading)
                    }
                    Button {
                        Task { await submit() }
                    } label: {
                        HStack {
                            if session.isWorking { ProgressView().tint(.white) }
                            Text(session.isWorking ? "Connecting securely…" : (registering ? "Create account" : "Sign in"))
                                .fontWeight(.semibold)
                        }
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 14)
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(session.isWorking || username.isEmpty || password.isEmpty || (registering && displayName.isEmpty))
                    Button(registering ? "Already have an account? Sign in" : "Create an account") {
                        registering.toggle()
                        session.errorMessage = nil
                    }
                    .font(.footnote)
                }
                .padding(.horizontal, 27)
                Text("TLS · scrypt-protected passwords · no App Store account")
                    .font(.caption2).foregroundStyle(.tertiary).padding(.top, 8)
                Spacer()
            }
            .navigationBarHidden(true)
            .background(Color(red: 0.055, green: 0.065, blue: 0.09).ignoresSafeArea())
        }
    }

    private func submit() async {
        do {
            try await session.authenticate(
                username: username.trimmingCharacters(in: .whitespacesAndNewlines),
                displayName: displayName.trimmingCharacters(in: .whitespacesAndNewlines),
                password: password,
                registering: registering)
            password = ""
        } catch {
            session.errorMessage = error.localizedDescription
        }
    }
}

struct HomeView: View {
    @EnvironmentObject private var session: SessionStore

    var body: some View {
        NavigationStack {
            List {
                Section {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Hello, \(session.user?.displayName ?? session.user?.username ?? "there")")
                            .font(.title2.weight(.semibold))
                        HStack {
                            Circle().fill(session.connectionStatus == "Connected" ? Color.green : Color.orange).frame(width: 8, height: 8)
                            Text(session.connectionStatus).font(.subheadline).foregroundStyle(.secondary)
                        }
                    }
                    .padding(.vertical, 7)
                }
                if session.isWorking || session.transferFraction > 0 && session.transferFraction < 1 {
                    Section("Active transfer") {
                        ProgressView(value: min(1, max(0, session.transferFraction)))
                            .tint(.purple)
                        HStack {
                            Text(session.activeTransferLabel).font(.caption).foregroundStyle(.secondary)
                            Spacer()
                            Text(session.activeTransferDetail).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
                if let message = session.errorMessage {
                    Section { Text(message).font(.footnote).foregroundStyle(.red) }
                }
                if !session.savedFiles.isEmpty {
                    Section("Received") {
                        ForEach(session.savedFiles, id: \.self) { url in
                            VStack(alignment: .leading) {
                                Text(url.lastPathComponent)
                                Text(url.path).font(.caption2).foregroundStyle(.secondary).lineLimit(2)
                            }
                        }
                    }
                }
                Section("Transfer") {
                    NavigationLink { TransferListView() } label: { Label("Send files", systemImage: "arrow.up.doc") }
                    NavigationLink { TransferHistoryView() } label: { Label("Transfers", systemImage: "clock.arrow.circlepath") }
                }
                Section("Devices") {
                    NavigationLink { DevicesView() } label: { Label("Devices & pairing", systemImage: "iphone") }
                }
                Section("Connect") {
                    NavigationLink { FriendsView() } label: { Label("Friends", systemImage: "person.2") }
                    NavigationLink { ChatListView() } label: { Label("Chat", systemImage: "bubble.left.and.bubble.right") }
                }
                Section("More") {
                    NavigationLink { SettingsView() } label: { Label("Settings", systemImage: "gearshape") }
                }
            }
            .navigationTitle("HoshinoTransfer")
            .toolbar { ToolbarItem(placement: .navigationBarTrailing) { Text(session.user?.username ?? "").font(.caption).foregroundStyle(.secondary) } }
            .refreshable { await session.refreshAll() }
        }
    }
}

struct TransferListView: View {
    @EnvironmentObject private var session: SessionStore
    @State private var showPicker = false
    @State private var targetFriendId: String = ""
    @State private var didAutoSelect = false

    private var acceptedFriends: [FriendRecord] {
        session.friends.filter { $0.state == "Accepted" }
    }

    private var target: FriendRecord? {
        acceptedFriends.first { $0.user.id == targetFriendId } ?? acceptedFriends.first
    }

    var body: some View {
        List {
            Section {
                if acceptedFriends.isEmpty {
                    Text("Accept a friend first to enable transfers.")
                        .font(.footnote).foregroundStyle(.secondary)
                } else {
                    Picker("Send to", selection: $targetFriendId) {
                        ForEach(acceptedFriends) { friend in
                            Text(friend.user.displayName).tag(friend.user.id)
                        }
                    }
                    Button {
                        showPicker = true
                    } label: {
                        Label("Choose files to send", systemImage: "square.and.arrow.up")
                            .frame(maxWidth: .infinity)
                    }
                    .disabled(target == nil)
                    if let target {
                        Text("Files will be sent to \(target.user.displayName). Direct Wi-Fi is tried first when both devices share a LAN, otherwise the Server Relay is used automatically.")
                            .font(.footnote).foregroundStyle(.secondary)
                    }
                }
            }
            if session.isWorking || session.transferFraction > 0 {
                Section("In progress") {
                    ProgressView(value: min(1, max(0, session.transferFraction))).tint(.purple)
                    Text(session.activeTransferDetail).font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .navigationTitle("Send files")
        .onAppear {
            guard !didAutoSelect else { return }
            didAutoSelect = true
            targetFriendId = acceptedFriends.first?.user.id ?? ""
        }
        .fileImporter(isPresented: $showPicker, allowedContentTypes: [.item], allowsMultipleSelection: true) { result in
            guard case .success(let urls) = result, !urls.isEmpty,
                  let friend = target else { return }
            Task { await session.sendFiles(urls, to: friend.user.id) }
        }
    }
}

struct TransferHistoryView: View {
    @EnvironmentObject private var session: SessionStore

    var body: some View {
        List {
            ForEach(session.transfers) { transfer in
                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text(transfer.transport).font(.caption.weight(.semibold)).foregroundStyle(.green)
                        Spacer()
                        Text(transfer.status).font(.caption).foregroundStyle(.purple)
                    }
                    ForEach(transfer.items) { item in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(item.fileName).font(.subheadline)
                            ProgressView(value: Double(item.receivedBytes ?? 0), total: Double(max(1, item.size)))
                                .tint(.purple)
                            Text("\(item.receivedBytes ?? 0) of \(item.size) bytes · SHA-256 \(item.status)")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    HStack(spacing: 12) {
                        if transfer.status == "Pending" && transfer.receiverId == session.currentUserId {
                            Button("Accept") { Task { await session.acceptIncoming(transfer) } }.buttonStyle(.borderedProminent)
                            Button("Decline", role: .destructive) { Task { await session.declineIncoming(transfer) } }
                        }
                        if transfer.status == "Transferring" && transfer.senderId == session.currentUserId {
                            Button("Pause") { Task { await session.pauseTransfer(transfer) } }
                        }
                        if transfer.status == "Paused" && transfer.senderId == session.currentUserId {
                            Button("Resume") { Task { await session.resumeTransfer(transfer) } }
                        }
                        if ["Failed", "Cancelled"].contains(transfer.status) && transfer.senderId == session.currentUserId {
                            Button("Retry") { Task { await session.retryTransfer(transfer) } }
                        }
                        if ["Pending", "Transferring", "Paused"].contains(transfer.status) {
                            Button("Cancel", role: .destructive) { Task { await session.cancelTransfer(transfer) } }
                        }
                    }
                    .font(.caption)
                }
                .padding(.vertical, 4)
            }
        }
        .navigationTitle("Transfers")
        .refreshable { await session.refreshAll() }
    }
}

struct DevicesView: View {
    @EnvironmentObject private var session: SessionStore
    @State private var newDeviceName = ""
    @State private var pairingInput = ""

    var body: some View {
        Form {
            Section {
                Button {
                    Task { await session.createPairingCode() }
                } label: {
                    Label(session.pairingCode == nil ? "Create pairing code" : "Create a new code",
                          systemImage: "qrcode")
                }
                if let code = session.pairingCode {
                    PairingCodeCard(code: code, link: session.pairingLink(for: code.pairingCode))
                    Button("Clear", role: .destructive) { session.clearPairingCode() }
                        .font(.footnote)
                }
            } header: {
                Text("Pair another device")
            } footer: {
                Text("The code is single-use, expires in 5 minutes, and the second device must be signed in to this account.")
            }
            Section("Pair with a code from another device") {
                TextField("Eight-digit code", text: $pairingInput)
                    .keyboardType(.numberPad)
                    .onChange(of: pairingInput) { _ in
                        let digits = String(pairingInput.filter(\.isNumber).prefix(8))
                        if digits != pairingInput { pairingInput = digits }
                    }
                TextField("This device name", text: $newDeviceName)
                Button("Pair") {
                    Task { await session.pairDevice(code: pairingInput, name: newDeviceName.isEmpty ? UIDevice.current.name : newDeviceName) }
                }
                .disabled(pairingInput.count != 8 || session.isWorking)
                NavigationLink { PairByQRView() } label: {
                    Label("Scan or import a pairing QR", systemImage: "qrcode.viewfinder")
                }
            }
            Section("Registered devices") {
                ForEach(session.devices) { device in
                    HStack {
                        VStack(alignment: .leading) {
                            Text(device.deviceName)
                            Text(device.platform).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Text(device.status ?? (device.online == true ? "Online" : "Offline"))
                            .font(.caption)
                            .foregroundStyle(device.online == true ? .green : .secondary)
                    }
                }
                .onDelete { indexes in
                    for index in indexes {
                        if session.devices.indices.contains(index) {
                            Task { await session.revokeDevice(session.devices[index]) }
                        }
                    }
                }
            }
        }
        .navigationTitle("Devices")
        .refreshable { await session.refreshAll() }
    }
}

struct FriendsView: View {
    @EnvironmentObject private var session: SessionStore
    @State private var query = ""

    var body: some View {
        Form {
            Section("Search by username") {
                TextField("At least 3 characters", text: $query)
                    .textInputAutocapitalization(.never)
                    .onSubmit { Task { await session.searchUsers(query) } }
                Button("Search") { Task { await session.searchUsers(query) } }
                ForEach(session.searchResults) { result in
                    HStack {
                        VStack(alignment: .leading) {
                            Text(result.displayName)
                            Text("@\(result.username)").font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("Add") { Task { await session.addFriend(result) } }.buttonStyle(.bordered)
                    }
                }
            }
            Section("People") {
                if session.friends.isEmpty { Text("No friend relationships yet.").foregroundStyle(.secondary) }
                ForEach(session.friends) { friend in
                    HStack {
                        VStack(alignment: .leading) {
                            HStack(spacing: 6) {
                                Circle().fill(friend.online ? Color.green : Color.gray).frame(width: 7, height: 7)
                                Text(friend.user.displayName)
                            }
                            Text("\(friend.user.username) · \(friend.direction)").font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Text(friend.state).font(.caption).foregroundStyle(.purple)
                    }
                    .swipeActions(edge: .trailing) {
                        Button(role: .destructive) { Task { await session.removeFriend(friend) } } label: { Label("Remove", systemImage: "trash") }
                        Button { Task { await session.setBlocked(friend, blocked: true) } } label: { Label("Block", systemImage: "hand.raised") }.tint(.orange)
                        if friend.state == "Pending" && friend.direction == "incoming" {
                            Button { Task { await session.answerFriendRequest(friend, accept: true) } } label: { Label("Accept", systemImage: "checkmark") }.tint(.green)
                        }
                        if friend.state == "Blocked" {
                            Button { Task { await session.setBlocked(friend, blocked: false) } } label: { Label("Unblock", systemImage: "checkmark.circle") }.tint(.blue)
                        }
                        if friend.state == "Accepted" {
                            Button { Task { await session.openChat(with: friend) } } label: { Label("Chat", systemImage: "bubble.left") }.tint(.purple)
                        }
                    }
                }
            }
        }
        .navigationTitle("Friends")
        .refreshable { await session.refreshAll() }
    }
}

struct ChatListView: View {
    @EnvironmentObject private var session: SessionStore

    var body: some View {
        List {
            if session.chats.isEmpty { Text("Open a friend conversation to start chatting.").foregroundStyle(.secondary) }
            ForEach(session.chats) { chat in
                NavigationLink { ChatView(chat: chat) } label: {
                    HStack {
                        Circle().fill(chat.online == true ? Color.green : Color.gray).frame(width: 8, height: 8)
                        VStack(alignment: .leading) {
                            Text(chat.user?.displayName ?? "Chat")
                            Text(chat.lastMessage ?? "No messages yet").font(.caption).foregroundStyle(.secondary).lineLimit(1)
                        }
                    }
                }
            }
        }
        .navigationTitle("Chat")
        .refreshable { await session.refreshAll() }
    }
}

struct ChatView: View {
    let chat: ChatRecord
    @EnvironmentObject private var session: SessionStore
    @State private var draft = ""
    @State private var showPicker = false

    var body: some View {
        VStack(spacing: 0) {
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 8) {
                        ForEach(session.messages) { message in
                            VStack(alignment: .leading, spacing: 3) {
                                Text(message.content).textSelection(.enabled)
                                HStack {
                                    Text(message.timestamp).font(.caption2).foregroundStyle(.secondary)
                                    if message.senderId == session.currentUserId {
                                        Text(message.status).font(.caption2).foregroundStyle(.purple)
                                    }
                                }
                            }
                            .padding(12)
                            .background(RoundedRectangle(cornerRadius: 12).fill(Color.white.opacity(0.06)))
                            .padding(.horizontal, 12)
                            .id(message.id)
                        }
                    }
                    .padding(.vertical, 10)
                }
                .onChange(of: session.messages.count) { _ in
                    if let last = session.messages.last { proxy.scrollTo(last.id, anchor: .bottom) }
                }
            }
            HStack {
                Button { showPicker = true } label: { Image(systemName: "paperclip") }
                    .buttonStyle(.bordered)
                TextField("Message", text: $draft, axis: .vertical)
                    .textFieldStyle(.roundedBorder)
                    .lineLimit(1...4)
                    .onChange(of: draft) { _ in session.typingChanged(!draft.isEmpty) }
                Button {
                    let content = draft.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !content.isEmpty else { return }
                    draft = ""
                    Task { await session.sendMessage(content) }
                } label: { Image(systemName: "paperplane.fill") }
                    .buttonStyle(.borderedProminent)
                    .disabled(draft.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            .padding(12)
        }
        .navigationTitle(chat.user?.displayName ?? "Chat")
        .task {
            await session.selectChat(chat)
        }
        .fileImporter(isPresented: $showPicker, allowedContentTypes: [.item], allowsMultipleSelection: true) { result in
            guard case .success(let urls) = result, !urls.isEmpty else { return }
            Task { await session.sendFiles(urls, to: chat.userId ?? chat.user?.id ?? "", in: chat) }
        }
    }
}

struct TransferRequestSheet: View {
    @EnvironmentObject private var session: SessionStore
    let transfer: TransferRecord
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 18) {
            Image(systemName: "arrow.down.doc").font(.system(size: 40)).foregroundStyle(.tint)
            Text("Incoming transfer").font(.title2.weight(.semibold))
            ForEach(transfer.items) { item in
                VStack(alignment: .leading) {
                    Text(item.fileName)
                    Text("\(item.size) bytes · SHA-256 verified on completion").font(.caption).foregroundStyle(.secondary)
                }
            }
            Text("Transport: \(transfer.transport)").font(.caption).foregroundStyle(.secondary)
            HStack {
                Button("Decline", role: .destructive) {
                    Task { await session.declineIncoming(transfer); dismiss() }
                }
                .buttonStyle(.bordered)
                Button("Accept & receive") {
                    Task { await session.acceptIncoming(transfer); dismiss() }
                }
                .buttonStyle(.borderedProminent)
            }
            if let message = session.errorMessage {
                Text(message).font(.footnote).foregroundStyle(.red)
            }
        }
        .padding(26)
    }
}

struct SettingsView: View {
    @EnvironmentObject private var session: SessionStore
    @State private var checking = false
    @State private var deviceName = UIDevice.current.name

    var body: some View {
        Form {
            Section("Account") {
                LabeledContent("Username", value: session.user?.username ?? "—")
                LabeledContent("Display name", value: session.user?.displayName ?? "—")
                Button("Sign out", role: .destructive) { Task { await session.signOut() } }
            }
            Section("This device") {
                TextField("Device name", text: $deviceName)
                Button(session.isWorking ? "Registering…" : "Register / refresh device") {
                    Task { await session.registerCurrentDevice(name: deviceName) }
                }
                .disabled(session.isWorking || deviceName.isEmpty)
            }
            Section("Service") {
                LabeledContent("Production endpoint", value: session.serviceHost)
                LabeledContent("Connection", value: session.serviceStatus)
                Button(checking ? "Checking…" : "Check connection") {
                    checking = true
                    defer { checking = false }
                    Task {
                        do { session.serviceStatus = try await session.checkHealth() }
                        catch { session.serviceStatus = error.localizedDescription }
                    }
                }
                .disabled(checking)
            }
            Section("Transfers") {
                LabeledContent("Channel order", value: "P2P → Direct Wi-Fi → Server Relay")
                Text("Bytes move device-to-device over UDP hole punching or a shared LAN, and fall back to the Server Relay automatically. Lightning cable is unavailable and is never reported as active.")
                    .font(.footnote).foregroundStyle(.secondary)
            }
            Section("About") {
                LabeledContent("Build", value: "Unsigned sideload (LiveContainer)")
                Text("No StoreKit, App Store receipt or App Store Connect dependency. Sessions live in the device Keychain.")
                    .font(.footnote).foregroundStyle(.secondary)
            }
        }
        .navigationTitle("Settings")
    }
}
