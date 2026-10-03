import AVFoundation
import SwiftUI

final class QRScannerCoordinator: NSObject, AVCaptureMetadataOutputObjectsDelegate {
    let onCode: (String) -> Void

    init(onCode: @escaping (String) -> Void) {
        self.onCode = onCode
    }

    func metadataOutput(_ output: AVCaptureMetadataOutput, didOutput metadataObjects: [AVMetadataObject], from connection: AVCaptureConnection) {
        guard let object = metadataObjects.compactMap({ $0 as? AVMetadataMachineReadableCodeObject }).first,
              object.type == .qr,
              let value = object.stringValue else { return }
        onCode(value)
    }
}

struct CameraScannerView: UIViewRepresentable {
    let onCode: (String) -> Void
    @Binding var failed: Bool

    final class ScannerHostView: UIView {
        let session = AVCaptureSession()
        var coordinator: QRScannerCoordinator?
    }

    func makeUIView(context: Context) -> ScannerHostView {
        let view = ScannerHostView()
        guard let device = AVCaptureDevice.default(for: .video),
              let input = try? AVCaptureDeviceInput(device: device),
              view.session.canAddInput(input) else {
            failed = true
            return view
        }
        view.session.addInput(input)
        let output = AVCaptureMetadataOutput()
        guard view.session.canAddOutput(output) else {
            failed = true
            return view
        }
        view.session.addOutput(output)
        output.setMetadataObjectsDelegate(context.coordinator, queue: .main)
        output.metadataObjectTypes = [.qr]
        let preview = AVCaptureVideoPreviewLayer(session: view.session)
        preview.videoGravity = .resizeAspectFill
        preview.frame = UIScreen.main.bounds
        view.layer.addSublayer(preview)
        view.coordinator = context.coordinator
        DispatchQueue.global(qos: .userInitiated).async { view.session.startRunning() }
        return view
    }

    func updateUIView(_ uiView: ScannerHostView, context: Context) {}

    func makeCoordinator() -> QRScannerCoordinator {
        QRScannerCoordinator { code in
            DispatchQueue.main.async { onCode(code) }
        }
    }
}

struct PairByQRView: View {
    @EnvironmentObject private var session: SessionStore
    @Environment(\.dismiss) private var dismiss
    @State private var cameraFailed = false
    @State private var parsedCode: String?

    var body: some View {
        VStack(spacing: 16) {
            if cameraFailed {
                Image(systemName: "camera.on.rectangle").font(.system(size: 38)).foregroundStyle(.tint)
                Text("Camera unavailable").font(.headline)
                Text("Allow camera access or type the eight-digit code on the Devices screen.")
                    .font(.subheadline).foregroundStyle(.secondary).multilineTextAlignment(.center)
            } else {
                CameraScannerView(onCode: { raw in
                    guard parsedCode == nil, let code = Self.pairingCode(from: raw) else { return }
                    parsedCode = code
                }, failed: $cameraFailed)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .ignoresSafeArea()
            }
            if let code = parsedCode {
                VStack(spacing: 12) {
                    Text("Pairing code: \(code)").font(.title3.monospaced().weight(.bold))
                    Button("Pair this device") {
                        Task {
                            await session.pairDevice(code: code, name: UIDevice.current.name)
                            dismiss()
                        }
                    }
                    .buttonStyle(.borderedProminent)
                }
                .padding(.vertical, 24)
                .frame(maxWidth: .infinity)
                .background(.ultraThinMaterial)
            }
        }
        .navigationTitle("Scan pairing QR")
        .navigationBarTitleDisplayMode(.inline)
    }

    static func pairingCode(from raw: String) -> String? {
        if raw.hasPrefix("hoshinotransfer://pair?code=") {
            let code = String(raw.dropFirst("hoshinotransfer://pair?code=".count))
            return code.count == 8 && code.allSatisfy(\.isNumber) ? code : nil
        }
        return raw.count == 8 && raw.allSatisfy(\.isNumber) ? raw : nil
    }
}
