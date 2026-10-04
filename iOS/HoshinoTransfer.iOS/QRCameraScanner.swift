import AVFoundation
import CoreImage
import PhotosUI
import SwiftUI
import UIKit

/// Pairing QR payloads. The QR carries the API link of the service that issued the code so the
/// scanning device learns both which server to talk to and which code to redeem.
enum PairingPayload {
    static let pairRoute = "api/v1/devices/pair"

    static func link(base: URL, code: String) -> String {
        var components = URLComponents(url: base.appending(path: pairRoute), resolvingAgainstBaseURL: false)
        components?.queryItems = [URLQueryItem(name: "code", value: code)]
        return components?.url?.absoluteString ?? "\(base.absoluteString)/\(pairRoute)?code=\(code)"
    }

    /// Accepts the API link this app encodes, the older hoshinotransfer:// scheme, or a bare
    /// eight-digit code, so codes from either platform interoperate.
    static func extractCode(from raw: String) -> String? {
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }

        if let components = URLComponents(string: text),
           let value = components.queryItems?.first(where: { $0.name.lowercased() == "code" })?.value,
           isCode(value) {
            return value
        }

        let digits = text.filter(\.isNumber)
        if isCode(digits) { return digits }
        let tail = String(digits.suffix(8))
        return isCode(tail) ? tail : nil
    }

    static func isCode(_ value: String) -> Bool {
        value.count == 8 && value.allSatisfy(\.isNumber)
    }
}

/// Finds QR codes inside a still image, so a screenshot or photo of a pairing code can be
/// imported instead of scanning it live with the camera.
enum QRImageDetector {
    static func codes(in image: UIImage) -> [String] {
        guard let cgImage = image.cgImage else { return [] }
        let context = CIContext()
        guard let detector = CIDetector(ofType: CIDetectorTypeQRCode, using: context, options: [CIDetectorAccuracy: CIDetectorAccuracyHigh]) else { return [] }
        let features = detector.features(in: CIImage(cgImage: cgImage))
        return features.compactMap { ($0 as? CIQRCodeFeature)?.messageString }
    }
}

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
    @State private var detectedCode: String?
    @State private var statusText = ""
    @State private var photoItem: PhotosPickerItem?

    var body: some View {
        VStack(spacing: 16) {
            if cameraFailed {
                Image(systemName: "camera.on.rectangle").font(.system(size: 38)).foregroundStyle(.tint)
                Text("Camera unavailable").font(.headline)
                Text("Use a photo of the QR code below, or type the eight-digit code on the Devices screen.")
                    .font(.subheadline).foregroundStyle(.secondary).multilineTextAlignment(.center)
            } else {
                CameraScannerView(onCode: { raw in
                    guard detectedCode == nil else { return }
                    guard let code = PairingPayload.extractCode(from: raw) else { return }
                    detectedCode = code
                    redeem(code)
                }, failed: $cameraFailed)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .ignoresSafeArea()
            }

            if let code = detectedCode {
                VStack(spacing: 12) {
                    if session.isWorking {
                        ProgressView("Pairing \(code)…")
                    } else {
                        Text("Pairing code: \(code)").font(.title3.monospaced().weight(.bold))
                        Text(statusText.isEmpty ? "Pairing failed." : statusText)
                            .font(.footnote).foregroundStyle(.red).multilineTextAlignment(.center)
                        Button("Pair this device") {
                            redeem(code)
                        }
                        .buttonStyle(.borderedProminent)
                        Button("Scan again") {
                            detectedCode = nil; statusText = ""
                        }
                        .font(.footnote)
                    }
                }
                .padding(.vertical, 24)
                .frame(maxWidth: .infinity)
                .background(.ultraThinMaterial)
            } else {
                VStack(spacing: 10) {
                    PhotosPicker(selection: $photoItem, matching: .images) {
                        Label("Detect QR from a photo", systemImage: "photo.on.rectangle")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.bordered)
                    .onChange(of: photoItem) { _ in loadPhoto() }
                    if session.isWorking {
                        ProgressView("Reading the image…")
                    }
                    Text("Pick a screenshot or photo that contains the pairing QR code. The code is redeemed automatically.")
                        .font(.footnote).foregroundStyle(.secondary).multilineTextAlignment(.center)
                }
                .padding(.horizontal)
                .padding(.bottom, 12)
            }
        }
        .navigationTitle("Scan pairing QR")
        .navigationBarTitleDisplayMode(.inline)
    }

    private func loadPhoto() {
        guard let photoItem else { return }
        Task {
            defer { self.photoItem = nil }
            guard let data = try? await photoItem.loadTransferable(type: Data.self),
                  let image = UIImage(data: data) else {
                statusText = "That file could not be read as an image."
                return
            }
            let found = QRImageDetector.codes(in: image)
            guard let code = found.lazy.compactMap(PairingPayload.extractCode(from:)).first else {
                statusText = "No HoshinoTransfer pairing QR code was found in that image."
                return
            }
            detectedCode = code
            redeem(code)
        }
    }

    private func redeem(_ code: String) {
        Task {
            await session.pairDevice(code: code, name: UIDevice.current.name)
            statusText = session.errorMessage ?? ""
            if session.errorMessage == nil { dismiss() }
        }
    }
}
